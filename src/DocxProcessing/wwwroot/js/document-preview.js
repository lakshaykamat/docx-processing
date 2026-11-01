import { createElement } from './dom.js';

function formattingMap(values = []) {
  return new Map(values
    .filter(item => item.value != null)
    .map(item => [item.property, item.value]));
}

function applyParagraphFormatting(element, formatting) {
  const values = formattingMap(formatting);
  const alignment = values.get('Alignment');
  if (['left', 'center', 'right', 'both'].includes(alignment)) {
    element.style.textAlign = alignment === 'both' ? 'justify' : alignment;
  }

  const leftIndent = Number.parseFloat(values.get('Indent left'));
  const before = Number.parseFloat(values.get('Spacing before'));
  const after = Number.parseFloat(values.get('Spacing after'));
  if (Number.isFinite(leftIndent)) element.style.marginLeft = `${leftIndent / 20}pt`;
  if (Number.isFinite(before)) element.style.marginTop = `${before / 20}pt`;
  if (Number.isFinite(after)) element.style.marginBottom = `${after / 20}pt`;
}

function applyRunFormatting(element, formatting) {
  const values = formattingMap(formatting);
  const font = values.get('Font family (Latin)');
  const size = Number.parseFloat(values.get('Font size'));
  const color = values.get('Color');

  if (font && /^[\w ,.'"-]{1,80}$/.test(font)) element.style.fontFamily = font;
  if (Number.isFinite(size)) element.style.fontSize = `${size / 2}pt`;
  if (values.get('Bold') === 'true') element.style.fontWeight = '700';
  if (values.get('Italic') === 'true') element.style.fontStyle = 'italic';
  if (values.has('Underline') && values.get('Underline') !== 'none') {
    element.style.textDecoration = 'underline';
  }
  if (color && /^#?[0-9a-f]{6}$/i.test(color)) {
    element.style.color = color.startsWith('#') ? color : `#${color}`;
  }
}

function renderParagraph(node) {
  let paragraph;
  if (node.type === 'Heading') {
    const level = Math.min(Math.max(node.headingLevel || 1, 1), 6);
    paragraph = createElement(`h${level}`);
  } else if (node.type === 'Title') {
    paragraph = createElement('h1');
  } else {
    paragraph = createElement('p', null, node.type === 'Caption' ? 'doc-caption' : 'doc-paragraph');
  }

  paragraph.dataset.nodeId = node.id;
  if (node.styleId) paragraph.dataset.styleId = node.styleId;
  applyParagraphFormatting(paragraph, node.formatting);

  const runs = (node.runs || []).filter(run => run.text);
  if (!runs.length) {
    paragraph.textContent = node.text || '';
    return paragraph;
  }

  for (const run of runs) {
    const span = createElement('span', run.text);
    if (run.styleId) span.dataset.styleId = run.styleId;
    applyRunFormatting(span, run.formatting);
    paragraph.append(span);
  }
  return paragraph;
}

function renderTable(node) {
  const table = createElement('table', null, 'doc-table');
  table.dataset.nodeId = node.id;
  const body = createElement('tbody');

  for (const rowNode of node.children || []) {
    if (rowNode.type !== 'tr') {
      body.append(renderDocumentNode(rowNode));
      continue;
    }

    const row = createElement('tr');
    row.dataset.nodeId = rowNode.id;
    for (const cellNode of rowNode.children || []) {
      if (cellNode.type !== 'tc') continue;
      const cell = createElement('td');
      cell.dataset.nodeId = cellNode.id;
      for (const child of cellNode.children || []) cell.append(renderDocumentNode(child));
      if (!cellNode.children?.length && cellNode.text) cell.textContent = cellNode.text;
      row.append(cell);
    }
    body.append(row);
  }

  table.append(body);
  return table;
}

function renderDocumentNode(node) {
  if (node.type === 'Table') return renderTable(node);

  if (['Paragraph', 'Heading', 'Title', 'Caption'].includes(node.type)) {
    const content = renderParagraph(node);
    if (!node.children?.length) return content;
    const section = createElement('section', null, 'doc-section');
    section.append(content);
    node.children.forEach(child => section.append(renderDocumentNode(child)));
    return section;
  }

  if (node.children?.length) {
    const section = createElement('div', null, 'doc-section');
    section.dataset.nodeId = node.id;
    node.children.forEach(child => section.append(renderDocumentNode(child)));
    return section;
  }

  const element = createElement(node.text ? 'p' : 'div', node.text || '', 'doc-paragraph');
  element.dataset.nodeId = node.id;
  return element;
}

export function renderPreview(host, report, activeNodeId = null) {
  host.replaceChildren();
  const page = createElement('article', null, 'doc-page');
  (report.outline || []).forEach(node => page.append(renderDocumentNode(node)));
  if (!page.childNodes.length) {
    page.append(createElement('p', 'No main-body text was found in this document.', 'empty-state'));
  }
  host.append(page);

  if (activeNodeId) {
    host.querySelectorAll('[data-node-id]').forEach(item => {
      item.classList.toggle('doc-highlight', item.dataset.nodeId === activeNodeId);
    });
  }
}
