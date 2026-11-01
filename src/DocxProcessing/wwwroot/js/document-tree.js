import { createElement } from './dom.js';

function styleLabel(node) {
  const labels = {
    Heading: `H${node.headingLevel || ''}`,
    Title: 'Title',
    Caption: 'Caption',
    Table: 'Table',
    tr: 'Row',
    tc: 'Cell',
    Paragraph: 'Text'
  };
  return labels[node.type] || node.type || 'Content';
}

function displayStyleName(style) {
  if (!style) return '';
  return style.name && style.name !== style.id
    ? `${style.name} (${style.id})`
    : style.name || style.id;
}

function characterStyleSegments(node, usedStyles) {
  const segments = [];
  for (const run of node.runs || []) {
    const styleId = run.styleId || null;
    const previous = segments.at(-1);
    if (previous && previous.styleId === styleId) {
      previous.text += run.text || '';
      continue;
    }
    segments.push({ styleId, styleName: run.styleName, text: run.text || '' });
  }
  return segments.filter(segment => segment.styleId && usedStyles.has(segment.styleId));
}

function normalizeText(value) {
  return (value || '').trim().replace(/\s+/g, ' ');
}

function makeContentRow(node, blockStyle) {
  const row = createElement('span', null, 'content-row');
  row.append(createElement('span', styleLabel(node), 'content-kind'));

  const fullText = normalizeText(node.text);
  const excerpt = fullText.length > 130 ? `${fullText.slice(0, 127)}…` : fullText;
  const content = createElement(
    'span',
    excerpt || (node.type === 'Paragraph' ? '(Empty paragraph)' : node.type),
    'content-text'
  );
  if (fullText) content.title = fullText;
  row.append(content);

  if (blockStyle) {
    const assignment = createElement('span', null, 'style-assignment');
    const styleKind = node.type === 'Table'
      ? (node.hasExplicitStyle ? 'Table style' : 'Default table style')
      : (node.hasExplicitStyle ? 'Paragraph style' : 'Default paragraph style');
    assignment.append(createElement(
      'span',
      styleKind,
      'assignment-kind'
    ));
    assignment.append(createElement('span', displayStyleName(blockStyle), 'content-style'));
    row.append(assignment);
  }

  return row;
}

function makeRunStyleDetails(segments, styles, query) {
  if (!segments.length) return null;

  const details = createElement('details', null, 'run-style-details');
  details.append(createElement('summary', `Text and character styles (${segments.length} segments)`));
  details.append(createElement(
    'p',
    'Unlabeled text inherits the paragraph style or uses direct formatting.',
    'run-style-hint'
  ));

  const list = createElement('ul', null, 'run-style-list');
  for (const segment of segments) {
    const item = createElement('li', null, 'run-style-item');
    const text = normalizeText(segment.text) || '(non-text run)';
    const excerpt = text.length > 100 ? `${text.slice(0, 97)}…` : text;
    const snippet = createElement('span', excerpt, 'run-style-text');
    if (text !== '(non-text run)') snippet.title = text;
    const style = styles.get(segment.styleId);
    item.append(
      snippet,
      createElement('span', 'Character style', 'assignment-kind'),
      createElement('span', displayStyleName(style), 'content-style')
    );
    list.append(item);
  }

  details.append(list);
  details.open = !!query && segments.some(segment =>
    `${segment.text} ${segment.styleName || ''} ${segment.styleId}`.toLowerCase().includes(query)
  );
  return details;
}

function makeTreeBranch(node, context) {
  const { styles, query, activeNodeId, onSelect } = context;
  const blockStyle = node.styleId ? styles.get(node.styleId) : null;
  const runSegments = characterStyleSegments(node, styles);
  const children = (node.children || [])
    .map(child => makeTreeBranch(child, context))
    .filter(Boolean);

  const stylesText = [blockStyle, ...runSegments.map(segment => styles.get(segment.styleId))]
    .filter(Boolean)
    .map(style => `${style.name} ${style.id}`)
    .join(' ');
  const runsText = runSegments.map(segment => segment.text).join(' ');
  const searchText = `${node.text || ''} ${runsText} ${node.type || ''} ${stylesText}`.toLowerCase();
  if (query && !searchText.includes(query) && !children.length) return null;

  const item = createElement(
    'li',
    null,
    `content-tree-item${node.type === 'Heading' ? ' heading-item' : ''}`
  );
  const row = makeContentRow(node, blockStyle);
  row.dataset.nodeId = node.id;
  if (node.id === activeNodeId) row.classList.add('tree-selected');

  if (children.length) {
    const details = createElement('details', null, 'content-branch');
    const summary = createElement('summary', null, 'content-summary');
    summary.append(row);
    summary.addEventListener('click', () => onSelect(node, row));
    const childList = createElement('ul', null, 'content-tree');
    children.forEach(child => childList.append(child));
    details.append(summary, childList);
    details.open = query ? children.length > 0 : node.type === 'Heading';
    item.append(details);
  } else {
    const button = createElement('button', null, 'content-node');
    button.type = 'button';
    button.append(row);
    button.dataset.nodeId = node.id;
    button.addEventListener('click', () => onSelect(node, row));
    item.append(button);
  }

  const runDetails = makeRunStyleDetails(runSegments, styles, query);
  if (runDetails) item.append(runDetails);
  return item;
}

export function renderContentTree(host, report, filter, activeNodeId, onSelect) {
  host.replaceChildren();
  const usedStyles = new Map((report.styles || [])
    .filter(style => style.usage > 0)
    .map(style => [style.id, style]));
  if (!report.outline?.length) {
    host.append(createElement('p', 'No main-body content was found in this document.', 'empty-state'));
    return;
  }

  const context = {
    styles: usedStyles,
    query: filter.trim().toLowerCase(),
    activeNodeId,
    onSelect
  };
  const tree = createElement('ul', null, 'content-tree content-tree-root');
  report.outline
    .map(node => makeTreeBranch(node, context))
    .filter(Boolean)
    .forEach(node => tree.append(node));

  if (!tree.childNodes.length) {
    host.append(createElement('p', 'No matching content or styles.', 'empty-state'));
  } else {
    host.append(tree);
  }
}

function collectHeadings(nodes, result = []) {
  for (const node of nodes || []) {
    if (node.type === 'Heading') result.push(node);
    collectHeadings(node.children, result);
  }
  return result;
}

export function renderHeadingOrder(host, report, onSelect) {
  host.replaceChildren();
  const headings = collectHeadings(report?.outline || []);
  if (!headings.length) {
    host.append(createElement('li', 'No headings were detected in this document.', 'empty-state'));
    return 0;
  }

  for (const heading of headings) {
    const item = createElement('li', null, 'heading-order-item');
    item.style.setProperty('--depth', Math.max(0, (heading.headingLevel || 1) - 1));
    const button = createElement('button', null, 'heading-order-button');
    button.type = 'button';
    button.dataset.nodeId = heading.id;
    button.append(
      createElement('span', heading.headingNumber || '', 'heading-sequence'),
      createElement('span', normalizeText(heading.text), 'heading-order-text')
    );
    button.addEventListener('click', () => onSelect(heading, button));
    item.append(button);
    host.append(item);
  }
  return headings.length;
}

export function renderAllStyles(host, report) {
  host.replaceChildren();
  const groups = report?.styleGroups || [];
  if (!groups.length) {
    host.append(createElement('p', 'No styles were defined in this document.', 'empty-state'));
    return;
  }

  for (const group of groups) {
    const section = createElement('section', null, 'style-group');
    const heading = createElement('div', null, 'style-group-heading');
    heading.append(
      createElement('h3', group.label),
      createElement('span', String(group.styles.length), 'style-count')
    );
    const list = createElement('ul', null, 'style-tree');

    for (const style of group.styles) {
      const item = createElement('li', null, 'style-tree-item');
      const row = createElement('div', null, 'style-node');
      row.append(createElement('span', style.name || style.id, 'style-name'));
      const meta = createElement('span', null, 'style-meta');
      meta.append(createElement('code', style.id, 'style-id'));
      meta.append(createElement('span', style.usage > 0 ? `Used ${style.usage}` : 'Unused', 'style-usage'));
      if (style.isDefault) meta.append(createElement('span', 'Default', 'style-default'));
      row.append(meta);
      item.append(row);
      list.append(item);
    }

    section.append(heading, list);
    host.append(section);
  }
}

export function selectDocumentNode(node, row) {
  document.querySelectorAll('#style-tree [data-node-id], #heading-order [data-node-id]')
    .forEach(item => item.classList.toggle('tree-selected', item.dataset.nodeId === node.id));
  document.querySelectorAll('#document-preview [data-node-id]')
    .forEach(item => item.classList.toggle('doc-highlight', item.dataset.nodeId === node.id));
  row?.classList.add('tree-selected');
  $(`#document-preview [data-node-id="${CSS.escape(node.id)}"]`)
    ?.scrollIntoView({ block: 'center', behavior: 'smooth' });
}
