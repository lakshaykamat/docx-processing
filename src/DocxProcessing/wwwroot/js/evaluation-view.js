import { $, createElement } from './dom.js';

const STATUS_LABELS = {
  likely_correct: 'No issues flagged',
  review_recommended: 'Review flagged items',
  insufficient_evidence: 'Not enough evidence'
};

export function renderEvaluation(result, nodesById, onLocate) {
  const badge = $('#evaluation-badge');
  badge.textContent = STATUS_LABELS[result.overallStatus] || 'Evaluation complete';
  badge.className = `evaluation-badge ${result.overallStatus || ''}`;
  $('#evaluation-summary-text').textContent = result.summary || 'No summary was returned.';
  $('#evaluation-coverage').textContent = result.coverageNote || '';

  const list = $('#evaluation-findings');
  list.replaceChildren();
  const findings = result.findings || [];
  if (!findings.length) {
    list.append(createElement(
      'li',
      'No likely style/text mismatches were flagged for the evaluated items.',
      'evaluation-empty'
    ));
  }

  for (const finding of findings) {
    const item = createElement('li', null, 'evaluation-finding');
    const heading = createElement('div', null, 'finding-heading');
    heading.append(createElement(
      'strong',
      finding.assessment === 'likely_mismatch' ? 'Likely mismatch' : 'Worth reviewing',
      'finding-type'
    ));
    item.append(heading);
    if (finding.contentExcerpt) {
      item.append(createElement('blockquote', `“${finding.contentExcerpt}”`, 'finding-excerpt'));
    }
    item.append(createElement('p', `Applied style: ${finding.appliedStyle || 'not identified'}`, 'finding-applied'));
    item.append(createElement('p', finding.reasoning || '', 'finding-reasoning'));
    if (finding.location) item.append(createElement('code', finding.location, 'finding-location'));

    const node = nodesById.get(finding.nodeId);
    if (node) {
      const locate = createElement('button', 'Locate in document', 'secondary finding-locate');
      locate.type = 'button';
      locate.addEventListener('click', () => onLocate(node, finding.nodeId));
      item.append(locate);
    }
    list.append(item);
  }

  $('#evaluation-results').hidden = false;
}
