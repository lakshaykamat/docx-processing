'use strict';

let report;
let activeNodeId = null;
let stylesById = new Map();
let nodesById = new Map();
let contentStyleCount = 0;
let activeTreeView = 'content';
const $ = selector => document.querySelector(selector);

function el(tag, text, className) {
  const element = document.createElement(tag);
  if (text != null) element.textContent = text;
  if (className) element.className = className;
  return element;
}

function headingLevelFor(node) {
  if (Number.isInteger(node.headingLevel) && node.headingLevel > 0) return node.headingLevel;
  const ids = [node.styleId, ...(stylesById.get(node.styleId)?.chain || [])].filter(Boolean);
  const names = [node.styleName, ...ids.flatMap(id => [stylesById.get(id)?.name, id])].filter(Boolean);
  for (const name of names) {
    const match = String(name).trim().match(/(?:heading|head)\s*[_-]?\s*([1-9])$/i) || String(name).trim().match(/^h\s*[_-]?\s*([1-9])$/i);
    if (match) return Number(match[1]);
  }
  return null;
}

function organizeByDocumentHeadings(nodes) {
  const ordered = [];
  function flatten(items) {
    (items || []).forEach(node => {
      const level = headingLevelFor(node);
      if (node.type === 'Heading' || level) {
        ordered.push({ ...node, type: 'Heading', headingLevel: level || node.headingLevel, inferred: node.inferred || !node.headingLevel, children: [] });
        flatten(node.children);
      } else ordered.push(node);
    });
  }
  flatten(nodes);

  const roots = [];
  const stack = [];
  ordered.forEach(node => {
    const level = node.type === 'Heading' ? node.headingLevel : null;
    if (level) {
      while (stack.length && stack[stack.length - 1].headingLevel >= level) stack.pop();
      const parent = stack[stack.length - 1];
      if (parent) parent.children.push(node); else roots.push(node);
      stack.push(node);
    } else {
      const parent = stack[stack.length - 1];
      if (parent) parent.children.push(node); else roots.push(node);
    }
  });
  return roots;
}

function styleNodeLabel(node) {
  if (node.type === 'Heading') return `H${node.headingLevel || ''}`;
  if (node.type === 'Title') return 'Title';
  if (node.type === 'Caption') return 'Caption';
  if (node.type === 'Table') return 'Table';
  if (node.type === 'tr') return 'Row';
  if (node.type === 'tc') return 'Cell';
  if (node.type === 'Paragraph') return 'Text';
  return node.type || 'Content';
}

function displayStyleName(style) {
  if (!style) return '';
  return style.name && style.name !== style.id ? `${style.name} (${style.id})` : (style.name || style.id);
}

function characterStyleSegments(node, stylesInUse) {
  const segments = [];
  (node.runs || []).forEach(run => {
    const styleId = run.styleId || null;
    const previous = segments[segments.length - 1];
    if (previous && previous.styleId === styleId) {
      previous.text += run.text || '';
      previous.runCount++;
    } else segments.push({ styleId, styleName: run.styleName, text: run.text || '', runCount: 1 });
  });
  return segments.filter(segment => segment.styleId && stylesInUse.has(segment.styleId));
}

function selectContentNode(node, element) {
  activeNodeId = node.id;
  document.querySelectorAll('#style-tree [data-node-id], #heading-order [data-node-id]').forEach(item => {
    item.classList.toggle('tree-selected', item.dataset.nodeId === activeNodeId);
  });
  document.querySelectorAll('#document-preview [data-node-id]').forEach(item => {
    item.classList.toggle('doc-highlight', item.dataset.nodeId === activeNodeId);
  });
  element?.classList.add('tree-selected');
  document.querySelector(`#document-preview [data-node-id="${CSS.escape(activeNodeId)}"]`)
    ?.scrollIntoView({ block: 'center', behavior: 'smooth' });
}

function drawStyleTree(host, filter = '') {
  host.replaceChildren();
  const stylesInUse = new Map((report.styles || []).filter(style => style.usage > 0).map(style => [style.id, style]));
  const visibleStyleIds = new Set();
  function collectUsedStyles(node) {
    if (node.styleId && stylesInUse.has(node.styleId)) visibleStyleIds.add(node.styleId);
    (node.runs || []).forEach(run => { if (run.styleId && stylesInUse.has(run.styleId)) visibleStyleIds.add(run.styleId); });
    (node.children || []).forEach(collectUsedStyles);
  }
  (report.outline || []).forEach(collectUsedStyles);
  contentStyleCount = visibleStyleIds.size;
  if (activeTreeView === 'content') $('#style-count').textContent = `${contentStyleCount} used styles`;
  const query = filter.trim().toLowerCase();
  if (!report.outline?.length) {
    host.append(el('p', 'No main-body content was found in this document.', 'empty-state'));
    return;
  }

  function branch(node) {
    const blockStyle = node.styleId ? stylesInUse.get(node.styleId) : null;
    const runSegments = characterStyleSegments(node, stylesInUse);
    const children = (node.children || []).map(branch).filter(Boolean);
    const styleText = [blockStyle, ...runSegments.map(segment => stylesInUse.get(segment.styleId))]
      .filter(Boolean).map(style => `${style.name} ${style.id}`).join(' ');
    const runText = runSegments.map(segment => segment.text).join(' ');
    const ownText = `${node.text || ''} ${runText} ${node.type || ''} ${styleText}`.toLowerCase();
    const matches = !query || ownText.includes(query);
    if (query && !matches && !children.length) return null;

    const item = el('li', null, `content-tree-item${node.type === 'Heading' ? ' heading-item' : ''}`);
    const row = el('span', null, 'content-row');
    row.append(el('span', styleNodeLabel(node), 'content-kind'));
    const text = (node.text || '').trim().replace(/\s+/g, ' ');
    const excerpt = text.length > 130 ? `${text.slice(0, 127)}…` : text;
    const contentText = el('span', excerpt || (node.type === 'Paragraph' ? '(Empty paragraph)' : node.type), 'content-text');
    if (text) contentText.title = text;
    row.append(contentText);
    if (blockStyle) {
      const assignment = el('span', null, 'style-assignment');
      assignment.append(el('span', node.type === 'Table' ? 'Table style' : 'Paragraph style', 'assignment-kind'));
      assignment.append(el('span', displayStyleName(blockStyle), 'content-style'));
      row.append(assignment);
    }
    row.dataset.nodeId = node.id;
    if (node.id === activeNodeId) row.classList.add('tree-selected');

    if (children.length) {
      const details = el('details', null, 'content-branch');
      const summary = el('summary', null, 'content-summary');
      summary.append(row);
      summary.addEventListener('click', () => selectContentNode(node, row));
      const childList = el('ul', null, 'content-tree');
      children.forEach(child => childList.append(child));
      details.append(summary, childList);
      details.open = query ? children.length > 0 : node.type === 'Heading';
      item.append(details);
    } else {
      const button = el('button', null, 'content-node');
      button.type = 'button';
      button.append(row);
      button.dataset.nodeId = node.id;
      button.onclick = () => selectContentNode(node, row);
      item.append(button);
    }
    if (runSegments.length) {
      const runDetails = el('details', null, 'run-style-details');
      const runSummary = el('summary', `Text and character styles (${runSegments.length} segments)`);
      const runHint = el('p', 'Unlabeled text inherits the paragraph style or uses direct formatting.', 'run-style-hint');
      const runList = el('ul', null, 'run-style-list');
      runSegments.forEach(segment => {
        const runItem = el('li', null, 'run-style-item');
        const snippet = segment.text.trim().replace(/\s+/g, ' ') || '(non-text run)';
        const runTextNode = el('span', snippet.length > 100 ? `${snippet.slice(0, 97)}…` : snippet, 'run-style-text');
        if (snippet !== '(non-text run)') runTextNode.title = snippet;
        const runStyle = stylesInUse.get(segment.styleId);
        runItem.append(runTextNode, el('span', 'Character style', 'assignment-kind'), el('span', displayStyleName(runStyle), 'content-style'));
        runList.append(runItem);
      });
      runDetails.append(runSummary, runHint, runList);
      runDetails.open = !!query && runSegments.some(segment => `${segment.text} ${segment.styleName || ''} ${segment.styleId}`.toLowerCase().includes(query));
      item.append(runDetails);
    }
    return item;
  }

  const tree = el('ul', null, 'content-tree content-tree-root');
  report.outline.map(branch).filter(Boolean).forEach(node => tree.append(node));
  if (!tree.childNodes.length) host.append(el('p', 'No matching content or styles.', 'empty-state'));
  else host.append(tree);
}

function headingNodes(nodes, result = []) {
  (nodes || []).forEach(node => {
    if (node.type === 'Heading') result.push(node);
    headingNodes(node.children, result);
  });
  return result;
}

function drawHeadingOrder(host) {
  host.replaceChildren();
  const headings = headingNodes(report?.outline || []);
  if (!headings.length) {
    host.append(el('li', 'No headings were detected in this document.', 'empty-state'));
    return 0;
  }
  const counters = [];
  headings.forEach(heading => {
    const level = Math.max(1, heading.headingLevel || 1);
    counters.length = Math.min(counters.length, level);
    while (counters.length < level) counters.push(0);
    for (let ancestor = 0; ancestor < level - 1; ancestor++) {
      if (counters[ancestor] === 0) counters[ancestor] = 1;
    }
    counters[level - 1]++;
    const order = counters.join('.');
    const item = el('li', null, 'heading-order-item');
    item.style.setProperty('--depth', level - 1);
    const button = el('button', null, 'heading-order-button');
    button.type = 'button';
    button.dataset.nodeId = heading.id;
    button.append(el('span', order, 'heading-sequence'));
    button.append(el('span', (heading.text || '').trim().replace(/\s+/g, ' '), 'heading-order-text'));
    button.addEventListener('click', () => selectContentNode(heading, button));
    item.append(button);
    host.append(item);
  });
  return headings.length;
}

function drawAllStyles(host) {
  host.replaceChildren();
  const groups = report?.styleGroups || [];
  if (!groups.length) {
    host.append(el("p", "No styles were defined in this document.", "empty-state"));
    return;
  }

  groups.forEach(group => {
    const section = el("section", null, "style-group");
    const heading = el("div", null, "style-group-heading");
    heading.append(el("h3", group.label));
    heading.append(el("span", String(group.styles.length), "style-count"));
    const list = el("ul", null, "style-tree");
    group.styles.forEach(style => {
      const item = el("li", null, "style-tree-item");
      const row = el("div", null, "style-node");
      row.append(el("span", style.name || style.id, "style-name"));
      const meta = el("span", null, "style-meta");
      meta.append(el("code", style.id, "style-id"));
      meta.append(el("span", style.usage > 0 ? "Used " + style.usage : "Unused", "style-usage"));
      if (style.isDefault) meta.append(el("span", "Default", "style-default"));
      row.append(meta);
      item.append(row);
      list.append(item);
    });
    section.append(heading, list);
    host.append(section);
  });
}
function setTreeView(view) {
  activeTreeView = view;
  const isHeadings = view === 'headings';
  const isAllStyles = view === 'styles';
  $('#tab-content').setAttribute('aria-selected', String(view === 'content'));
  $('#tab-headings').setAttribute('aria-selected', String(isHeadings));
  $('#tab-all-styles').setAttribute('aria-selected', String(isAllStyles));
  $('#content-view').hidden = view !== 'content';
  $('#heading-view').hidden = !isHeadings;
  $('#all-styles-view').hidden = !isAllStyles;
  if (isHeadings) $('#style-count').textContent = drawHeadingOrder($('#heading-order')) + ' headings';
  else if (isAllStyles) $('#style-count').textContent = (report?.counts?.['Styles defined'] || 0) + ' styles';
  else $('#style-count').textContent = contentStyleCount + ' used styles';
}

function formattingMap(values = []) {
  return new Map(values.map(item => [item.property, item.value]).filter(([, value]) => value != null));
}

function applyParagraphFormatting(element, formatting) {
  const values = formattingMap(formatting);
  const alignment = values.get('Alignment');
  if (['left', 'center', 'right', 'both'].includes(alignment)) element.style.textAlign = alignment === 'both' ? 'justify' : alignment;
  const leftIndent = parseFloat(values.get('Indent left'));
  if (Number.isFinite(leftIndent)) element.style.marginLeft = `${leftIndent / 20}pt`;
  const before = parseFloat(values.get('Spacing before'));
  const after = parseFloat(values.get('Spacing after'));
  if (Number.isFinite(before)) element.style.marginTop = `${before / 20}pt`;
  if (Number.isFinite(after)) element.style.marginBottom = `${after / 20}pt`;
}

function applyRunFormatting(element, formatting) {
  const values = formattingMap(formatting);
  const font = values.get('Font family (Latin)');
  if (font && /^[\w ,.'"-]{1,80}$/.test(font)) element.style.fontFamily = font;
  const size = parseFloat(values.get('Font size'));
  if (Number.isFinite(size)) element.style.fontSize = `${size / 2}pt`;
  if (values.get('Bold') === 'true') element.style.fontWeight = '700';
  if (values.get('Italic') === 'true') element.style.fontStyle = 'italic';
  if (values.has('Underline') && values.get('Underline') !== 'none') element.style.textDecoration = 'underline';
  const color = values.get('Color');
  if (color && /^#?[0-9a-f]{6}$/i.test(color)) element.style.color = color.startsWith('#') ? color : `#${color}`;
}

function renderParagraph(node) {
  let paragraph;
  if (node.type === 'Heading') paragraph = el(`h${Math.min(Math.max(node.headingLevel || 1, 1), 6)}`);
  else if (node.type === 'Title') paragraph = el('h1');
  else paragraph = el('p', null, node.type === 'Caption' ? 'doc-caption' : 'doc-paragraph');
  paragraph.dataset.nodeId = node.id;
  if (node.styleId) paragraph.dataset.styleId = node.styleId;
  applyParagraphFormatting(paragraph, node.formatting);

  const runs = (node.runs || []).filter(run => run.text);
  if (runs.length) {
    runs.forEach(run => {
      const span = el('span', run.text);
      if (run.styleId) span.dataset.styleId = run.styleId;
      applyRunFormatting(span, run.formatting);
      paragraph.append(span);
    });
  } else paragraph.textContent = node.text || '';
  return paragraph;
}

function renderTable(node) {
  const table = el('table', null, 'doc-table');
  table.dataset.nodeId = node.id;
  const body = el('tbody');
  (node.children || []).forEach(rowNode => {
    if (rowNode.type !== 'tr') { body.append(renderDocumentNode(rowNode)); return; }
    const row = el('tr');
    row.dataset.nodeId = rowNode.id;
    (rowNode.children || []).forEach(cellNode => {
      if (cellNode.type !== 'tc') return;
      const cell = el('td');
      cell.dataset.nodeId = cellNode.id;
      (cellNode.children || []).forEach(child => cell.append(renderDocumentNode(child)));
      if (!cellNode.children?.length && cellNode.text) cell.textContent = cellNode.text;
      row.append(cell);
    });
    body.append(row);
  });
  table.append(body);
  return table;
}

function renderDocumentNode(node) {
  if (node.type === 'Table') return renderTable(node);
  if (node.type === 'Paragraph' || node.type === 'Heading' || node.type === 'Title' || node.type === 'Caption') {
    const content = renderParagraph(node);
    if (!node.children?.length) return content;
    const section = el('section', null, 'doc-section');
    section.append(content);
    node.children.forEach(child => section.append(renderDocumentNode(child)));
    return section;
  }
  if (node.children?.length) {
    const section = el('div', null, 'doc-section');
    section.dataset.nodeId = node.id;
    node.children.forEach(child => section.append(renderDocumentNode(child)));
    return section;
  }
  const paragraph = el(node.text ? 'p' : 'div', node.text || '', 'doc-paragraph');
  paragraph.dataset.nodeId = node.id;
  return paragraph;
}

function renderPreview(host) {
  host.replaceChildren();
  const page = el('article', null, 'doc-page');
  (report.outline || []).forEach(node => page.append(renderDocumentNode(node)));
  if (!page.childNodes.length) page.append(el('p', 'No main-body text was found in this document.', 'empty-state'));
  host.append(page);
  if (activeNodeId) {
    document.querySelectorAll('#document-preview [data-node-id]').forEach(item => {
      item.classList.toggle('doc-highlight', item.dataset.nodeId === activeNodeId);
    });
  }
}

function renderStyleEvaluation(result) {
  const labels = {
    likely_correct: 'No issues flagged',
    review_recommended: 'Review flagged items',
    insufficient_evidence: 'Not enough evidence'
  };
  const badge = $('#evaluation-badge');
  badge.textContent = labels[result.overallStatus] || 'Evaluation complete';
  badge.className = `evaluation-badge ${result.overallStatus || ''}`;
  $('#evaluation-summary-text').textContent = result.summary || 'No summary was returned.';
  $('#evaluation-coverage').textContent = result.coverageNote || '';

  const list = $('#evaluation-findings');
  list.replaceChildren();
  const findings = result.findings || [];
  if (!findings.length) {
    list.append(el('li', 'No likely style/text mismatches were flagged for the evaluated items.', 'evaluation-empty'));
  }
  findings.forEach(finding => {
    const item = el('li', null, 'evaluation-finding');
    const heading = el('div', null, 'finding-heading');
    heading.append(el('strong', finding.assessment === 'likely_mismatch' ? 'Likely mismatch' : 'Worth reviewing', 'finding-type'));
    item.append(heading);
    if (finding.contentExcerpt) item.append(el('blockquote', `“${finding.contentExcerpt}”`, 'finding-excerpt'));
    item.append(el('p', `Applied style: ${finding.appliedStyle || 'not identified'}`, 'finding-applied'));
    item.append(el('p', finding.reasoning || '', 'finding-reasoning'));
    if (finding.location) item.append(el('code', finding.location, 'finding-location'));

    const node = nodesById.get(finding.nodeId);
    if (node) {
      const locate = el('button', 'Locate in document', 'secondary finding-locate');
      locate.type = 'button';
      locate.addEventListener('click', () => {
        const treeItem = document.querySelector(`#style-tree [data-node-id="${CSS.escape(finding.nodeId)}"]`);
        selectContentNode(node, treeItem);
      });
      item.append(locate);
    }
    list.append(item);
  });
  $('#evaluation-results').hidden = false;
}

$('#style-search').addEventListener('input', event => drawStyleTree($('#style-tree'), event.target.value));
$('#expand-all').addEventListener('click', () => document.querySelectorAll('#style-tree details').forEach(item => { item.open = true; }));
$('#collapse-all').addEventListener('click', () => document.querySelectorAll('#style-tree details').forEach(item => { item.open = false; }));
$('#tab-content').addEventListener('click', () => setTreeView('content'));
$('#tab-headings').addEventListener('click', () => setTreeView('headings'));
$('#tab-all-styles').addEventListener('click', () => setTreeView('styles'));
document.querySelector('[role="tablist"]').addEventListener('keydown', event => {
  if (!['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
  const tabs = [...event.currentTarget.querySelectorAll('[role="tab"]')];
  const current = tabs.indexOf(event.target.closest('[role="tab"]'));
  if (current < 0) return;
  event.preventDefault();
  const step = event.key === 'ArrowRight' ? 1 : -1;
  const nextTab = tabs[(current + step + tabs.length) % tabs.length];
  nextTab.focus();
  setTreeView(nextTab.id === 'tab-content' ? 'content' : nextTab.id === 'tab-headings' ? 'headings' : 'styles');
});

$('#evaluate-style').addEventListener('click', async () => {
  const status = $('#evaluation-status'), button = $('#evaluate-style'), file = $('#file').files[0];
  if (!file || !report) return;
  button.disabled = true;
  $('#evaluation-results').hidden = true;
  status.className = '';
  status.textContent = 'Sending document text and style details to OpenAI for review…';
  try {
    const response = await fetch('/api/evaluate-style', { method: 'POST', body: new FormData($('#upload')) });
    const data = await response.json().catch(() => { throw new Error('The style evaluation service returned an unreadable response.'); });
    if (!response.ok) throw new Error(data.error || 'Style evaluation failed.');
    renderStyleEvaluation(data);
    status.textContent = 'Evaluation complete. Treat the findings as a review aid.';
  } catch (error) {
    status.textContent = error.message;
    status.className = 'error';
  } finally {
    button.disabled = false;
  }
});

$('#upload').addEventListener('submit', async event => {
  event.preventDefault();
  const status = $('#status'), submit = $('#submit'), file = $('#file').files[0];
  if (!file) return;
  if (!/\.docx$/i.test(file.name) || file.size === 0 || file.size > 20 * 1024 * 1024) {
    status.textContent = 'Choose a nonempty .docx file up to 20 MB.';
    status.className = 'error';
    return;
  }

  submit.disabled = true;
  status.className = '';
  status.textContent = 'Opening document…';
  $('#report').hidden = true;
  try {
    const response = await fetch('/api/inspect', { method: 'POST', body: new FormData($('#upload')) });
    const data = await response.json().catch(() => { throw new Error('The server could not inspect this document.'); });
    if (!response.ok) throw new Error(data.error || 'Document inspection failed.');
    report = data;
    stylesById = new Map((report.styles || []).map(style => [style.id, style]));
    report.outline = organizeByDocumentHeadings(report.outline || []);
    nodesById = new Map();
    const collectNodes = nodes => (nodes || []).forEach(node => { nodesById.set(node.id, node); collectNodes(node.children); });
    collectNodes(report.outline);
    activeNodeId = null;
    $('#filename').textContent = data.filename;
    $('#filesize').textContent = `${(data.size / 1024).toFixed(1)} KB`;
    drawStyleTree($('#style-tree'));
    drawAllStyles($('#all-styles-list'));
    setTreeView('content');
    renderPreview($('#document-preview'));
    $('#evaluation-panel').hidden = false;
    $('#evaluation-results').hidden = true;
    $('#evaluation-status').textContent = '';
    $('#report').hidden = false;
    status.textContent = 'Document opened. All defined styles are listed in the All styles tab.';
  } catch (error) {
    status.textContent = error.message;
    status.className = 'error';
  } finally {
    submit.disabled = false;
  }
});
