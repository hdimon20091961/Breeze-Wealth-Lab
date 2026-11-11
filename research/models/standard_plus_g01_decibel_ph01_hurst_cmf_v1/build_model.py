"""Publish the user-accepted PH01/Hurst-CMF overlay without refitting any weights.

The preceding accepted model, experiments, live sources and rules stay immutable.
The current-model pointer is updated only after the replay checks pass.
"""
import copy
import csv
import hashlib
import json
import sys
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parent
PROJECT = ROOT.parents[2]
EXPERIMENT = PROJECT / 'research/artifacts/podhvat_candidate_experiment_v1'
PREVIOUS = ROOT.parent / 'standard_plus_g01_decibel_v1'
CATALOGUE_SOURCE = PROJECT / 'docs/event-catalog-v3/catalog.json'
CATALOGUE = PROJECT / 'docs/event-catalog-v4'
MODEL_ID = ROOT.name
sys.path.insert(0, str(EXPERIMENT))
from detectors_podhvat import load, features, detect
from calibration import apply_model, simulate, scalar_metrics


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), encoding='utf-8')


def table(path):
    with path.open(encoding='utf-8-sig', newline='') as handle:
        return list(csv.DictReader(handle))


def main():
    protected = list(PREVIOUS.glob('*.*')) + [CATALOGUE_SOURCE, PROJECT / 'Breeze/BreezeRules.json']
    protected += list((PROJECT / 'Breeze').glob('*.cs')) + list((PROJECT / 'Breeze').glob('*.xaml'))
    protected += [EXPERIMENT / name for name in (
        'selected_models.json', 'protocol.json', 'specification.md', 'walkforward_paths.csv',
        'walkforward_summary.csv', 'full_history_comparison.csv', 'detectors_podhvat.py')]
    protected += list((PROJECT / 'research/artifacts/event_hypotheses_xhigh_v1/baseline/data/Breeze').glob('*.csv'))
    protected += [PROJECT / 'research/artifacts/breadth_candidate_experiment_v1/detectors_breadth.py',
                  PROJECT / 'research/artifacts/sentiment_detector_experiment_v2/detectors.py',
                  PROJECT / 'research/artifacts/sentiment_detector_experiment_v2/calibration.py']
    before = {str(path): sha(path) for path in protected}
    previous = read(PREVIOUS / 'accepted_model.json')
    source = read(CATALOGUE_SOURCE)
    inventory = read(PROJECT / 'research/artifacts/event_hypotheses_xhigh_v1/catalog_inventory.json')
    original = {row['id']: row for row in inventory}
    live = {row['id']: row for row in read(PROJECT / 'Breeze/BreezeRules.json')['rules']}
    assert len(original) == 75 and set(live) == set(original)
    assert all(live[k]['breeze'] == original[k]['breeze'] for k in original)
    schedule = [m for m in read(EXPERIMENT / 'selected_models.json')
                if m['family'] == 'PH01' and m['mode'] == 'hurst_cmf']
    assert [m['year'] for m in schedule] == list(range(2020, 2027))
    assert schedule[-1]['weights'] == [10, 10, -20, -2.5]
    d = load()
    f = features(d)
    signals, starts, events, _ = detect(d, f)
    addition = np.zeros(len(d['date']))
    for model in schedule:
        mask = np.char.startswith(d['date'], str(model['year']))
        addition[mask] = apply_model(model, signals['PH01'], f)[mask]
    # Future continuation is explicit; no retrospective fit and no calendar expiry.
    after_schedule = d['date'] >= '2027-01-01'
    addition[after_schedule] = apply_model(schedule[-1], signals['PH01'], f)[after_schedule]
    score = np.clip(d['base'] + addition, 0, 100)
    valid = np.flatnonzero(np.isfinite(d['base']))
    expected = {row['Date']: float(row['PH01__hurst_cmf_score'])
                for row in table(EXPERIMENT / 'walkforward_paths.csv')}
    checked = [i for i in valid if d['date'][i] in expected]
    assert len(checked) == len(expected)
    assert np.allclose(score[checked], [expected[d['date'][i]] for i in checked], rtol=0, atol=1e-10)
    old_rows = {row['Date']: row for row in table(PREVIOUS / 'accepted_score.csv')}
    assert all(d['base'][i] == float(old_rows[d['date'][i]]['Score']) for i in valid)
    assert np.array_equal(score[valid[d['date'][valid] < '2020-01-01']],
                          d['base'][valid[d['date'][valid] < '2020-01-01']])
    with (ROOT / 'accepted_score.csv').open('w', encoding='utf-8-sig', newline='') as handle:
        writer = csv.writer(handle)
        writer.writerow(['Date', 'OriginalStandardScore', 'G01DecibelScore', 'PH01', 'HurstState',
                         'CMFState', 'HurstCMFState', 'PH01Weight', 'PH01Addition', 'Score',
                         'PreviousTarget', 'Target'])
        for i in valid:
            year = int(d['date'][i][:4])
            model = next((m for m in schedule if m['year'] == year), schedule[-1] if year > 2026 else None)
            state = int(f['regimes']['HC'][i])
            weight = model['weights'][state] if model and state >= 0 else 0
            writer.writerow([d['date'][i], d['original_base'][i], d['base'][i], signals['PH01'][i],
                             f['regimes']['H'][i], f['regimes']['C'][i], state, weight, addition[i],
                             score[i], 10 if d['base'][i] >= 50 else 0, 10 if score[i] >= 50 else 0])
    metrics = {}
    for name, mask in [('full', np.isfinite(d['base'])),
                       ('2020_2026', np.isfinite(d['base']) & (d['date'] >= '2020-01-01'))]:
        ids = np.flatnonzero(mask)
        metrics[name] = {key: scalar_metrics(simulate(values[ids], d['Open'][ids], d['Close'][ids], 2))
                         for key, values in [('previous', d['base']), ('accepted', score)]}
        metrics[name]['delta_profit'] = metrics[name]['accepted']['profit'] - metrics[name]['previous']['profit']
        metrics[name]['profit_improvement_pct'] = 100 * metrics[name]['delta_profit'] / metrics[name]['previous']['profit']
    tested = next(row for row in table(EXPERIMENT / 'walkforward_summary.csv') if row['name'] == 'PH01__hurst_cmf')
    full_tested = next(row for row in table(EXPERIMENT / 'full_history_comparison.csv') if row['name'] == 'PH01__hurst_cmf')
    for name, reference in [('full', full_tested), ('2020_2026', tested)]:
        for key in ('profit', 'max_dd_pct', 'max_dd_money', 'costs'):
            assert np.isclose(metrics[name]['accepted'][key], float(reference[key]), rtol=0, atol=1e-8)

    members = []
    for event in source['events']:
        key = event['id']
        member = {'id': key, 'name': event.get('name', event.get('title', key)),
                  'selected_for_scoring': key in original or key in ('S1-G01', 'PH01'),
                  'configured_weight_nonzero': False, 'current_score_weight': 0,
                  'status': 'retained_research_entry_zero_score_weight',
                  'definition_catalogue': str(CATALOGUE / 'catalog.json')}
        if key in original:
            config = original[key]['breeze']
            weight = config['weights']['standard'] if config['enabled'] else 0
            member.update(current_score_weight=weight, configured_weight_nonzero=weight != 0,
                          status='frozen_standard_rule', weight_unit='score_points')
        elif key == 'S1-G01':
            member.update(current_score_weight=None, current_weights=previous['schedule'][-1]['weights_low_high'],
                          configured_weight_nonzero=True, status='accepted_decibel_overlay',
                          weight_schedule=str(PREVIOUS / 'accepted_model.json'), weight_unit='score_points')
        elif key == 'PH01':
            member.update(current_score_weight=None, current_weights=schedule[-1]['weights'],
                          configured_weight_nonzero=True, status='accepted_hurst_cmf_overlay',
                          weight_schedule=str(ROOT / 'accepted_model.json'), weight_unit='score_points')
        members.append(member)
    counts = {'catalogue_entries': len(members), 'selected_scoring_rules_including_zero': sum(m['selected_for_scoring'] for m in members),
              'scoring_rules_with_nonzero_configured_weight': sum(m['configured_weight_nonzero'] for m in members),
              'original_standard_rules': len(original),
              'original_standard_nonzero': sum(m['configured_weight_nonzero'] for m in members if m['id'] in original),
              'accepted_overlay_rules': 2, 'selected_original_rules_with_zero_weight': 43,
              'retained_research_entries_outside_scoring': sum(not m['selected_for_scoring'] for m in members),
              'all_entries_without_nonzero_configured_weight': sum(not m['configured_weight_nonzero'] for m in members)}
    assert counts['catalogue_entries'] == 105 and counts['scoring_rules_with_nonzero_configured_weight'] == 34
    assert counts['selected_scoring_rules_including_zero'] == 77 and counts['retained_research_entries_outside_scoring'] == 28
    write(ROOT / 'model_members.json', {'model_id': MODEL_ID, 'counts': counts, 'members': members})
    manifest = {'id': MODEL_ID, 'accepted_by_user_date_moscow': '2026-10-25', 'scope': previous['scope'],
                'previous_accepted_model': str(PREVIOUS / 'accepted_model.json'), 'base': previous['base'],
                'composition': 'clip(previous accepted Standard+G01/Decibel score + PH01 * weight(Hurst-CMF state, annual schedule),0,100)',
                'event': {'catalogue_id': 'PH01', 'name': 'Подхват с памятью предшествующего роста',
                          'detector': str(EXPERIMENT / 'detectors_podhvat.py'), 'unchanged_since_experiment': True},
                'regime': {'state': '2*H+C', 'H': 'Hurst > median of preceding 252 bars',
                           'C': 'CMF > median of preceding 252 bars', 'equality': 'low',
                           'states': ['H low / CMF low', 'H low / CMF high', 'H high / CMF low', 'H high / CMF high'],
                           'unknown_state': 'zero supplement'},
                'schedule': schedule, 'before_2020': 'zero PH01 supplement; previous accepted score preserved',
                'continuation_from_2026': 'retain latest accepted weights [10,10,-20,-2.5]; no automatic refit',
                'original_rule_weights_changed': False, 'counts': counts,
                'members': str(ROOT / 'model_members.json'), 'catalogue': str(CATALOGUE / 'catalog.json'),
                'score_series': str(ROOT / 'accepted_score.csv'), 'score_series_sha256': sha(ROOT / 'accepted_score.csv'),
                'metrics_2bps': metrics,
                'PH01_event_starts': {'full': int(starts['PH01'][valid].sum()),
                                      '2020_2026': int(starts['PH01'][valid[d['date'][valid] >= '2020-01-01']].sum())},
                'count_semantics': 'Count rule IDs once; context cells, dates, video cards and historical starts are not extra rules. Nonzero weights need not contribute on every bar.',
                'execution': 'Unchanged: target 10 SPY units if score >=50, otherwise 0; next-session open; 2bps per side; capital 3000; no cash constraint.',
                'limitations': ['Best family/context chosen after examining 42 historical paths; this selection is not an untouched out-of-sample test.',
                               'Most gain occurs in 2026; weights in 2022-2025 remain zero.',
                               'Other PH and PX definitions retained, but no unaccepted scoring weights or limit execution policy added.',
                               'Research model materialized; runtime DLL deployment not performed.'],
                'inputs_sha256': before}
    write(ROOT / 'accepted_model.json', manifest)
    catalogue = copy.deepcopy(source)
    catalogue.update(version='event-catalog-v4', current=True, previous_catalogue=str(CATALOGUE_SOURCE),
                     accepted_baseline=str(ROOT / 'accepted_model.json'), accepted_model_counts=counts)
    by_id = {m['id']: m for m in members}
    for event in catalogue['events']:
        event['accepted_model_membership'] = by_id[event['id']]
        if event['id'] == 'PH01':
            event.update(accepted_in_research_base=True, catalogue_status='accepted_research_baseline_addition',
                         accepted_baseline=MODEL_ID, weight_schedule=str(ROOT / 'accepted_model.json'),
                         accepted_context='hurst_cmf', runtime_registered=False, live_model_promoted=False)
    CATALOGUE.mkdir(exist_ok=True)
    write(CATALOGUE / 'catalog.json', catalogue)
    rows = ['# Общий каталог Breeze · версия 4', '',
            '25.10.2026. Принят PH01 с Hurst–CMF поверх Standard + G01/Decibel. Все 105 записей версии 3 сохранены.', '',
            f'[Принятая расчётная модель]({(ROOT / "README.md").as_posix()}) · [Машинный каталог]({(CATALOGUE / "catalog.json").as_posix()})', '',
            'В расчёте 77 правил: 75 исходных и 2 добавки. Ненулевые веса имеют 34 правила: 32 исходных и 2 добавки. У 43 исходных вес 0; ещё 28 записей сохранены для исследования и не участвуют в оценке.', '',
            'Это число типов правил, а не число исторических срабатываний или карточек видео. Hurst/CMF — состояния одной добавки, они не считаются отдельными голосами. Рабочая DLL не обновлялась.', '',
            '| ID | Название | Участие в принятой модели | Текущий вес, пункты |', '|---|---|---|---|']
    for member in members:
        weight = member.get('current_weights', member['current_score_weight'])
        rows.append(f"| {member['id']} | {member['name']} | {member['status']} | {weight} |")
    (CATALOGUE / 'CATALOG.md').write_text('\n'.join(rows) + '\n', encoding='utf-8')
    assert before == {str(path): sha(path) for path in protected}
    write(ROOT / 'validation.json', {'passed': True, 'checked_test_dates': len(checked), 'full_score_rows': len(valid),
          'matches_tested_path': True, 'metrics_match_both_reference_windows': True, 'previous_model_and_runtime_unchanged': True,
          'all_105_catalogue_entries_retained': [e['id'] for e in source['events']] == [e['id'] for e in catalogue['events']],
          'counts': counts, 'protected_sha256': before})
    write(PROJECT / 'docs/event-catalog-current.json', {'version': 'event-catalog-v4',
          'path': str(CATALOGUE / 'catalog.json'), 'human_readable': str(CATALOGUE / 'CATALOG.md'), 'count': 105})
    write(PROJECT / 'research/models/current_model.json', {'id': MODEL_ID,
          'manifest': str(ROOT / 'accepted_model.json'), 'scope': manifest['scope']})
    print(json.dumps({'model': MODEL_ID, 'counts': counts, 'metrics': metrics, 'passed': True}, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
