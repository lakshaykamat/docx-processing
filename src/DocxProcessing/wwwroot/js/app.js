import { addDocumentStyles, evaluateDocument, inspectDocument } from './api.js';
import { $, createElement, setStatus } from './dom.js';
import { renderEvaluation } from './evaluation-view.js';
import { renderPreview } from './document-preview.js';
import {
  renderAllStyles,
  renderContentTree,
  renderHeadingOrder,
  selectDocumentNode
} from './document-tree.js';

let report = null;
let activeNodeId = null;
let contentStyleCount = 0;
let activeTreeView = 'content';
let nodesById = new Map();

function collectNodes(nodes, map = new Map()) {
  for (const node of nodes || []) {
    map.set(node.id, node);
    collectNodes(node.children, map);
  }
  return map;
}

function selectNode(node, row) {
  activeNodeId = node.id;
  selectDocumentNode(node, row);
}

function drawContentTree() {
  if (!report) return;
  renderContentTree(
    $('#style-tree'),
    report,
    $('#style-search').value,
    activeNodeId,
    selectNode
  );
  contentStyleCount = report.mainBodyStyleCount || 0;
  if (activeTreeView === 'content') {
    $('#style-count').textContent = `${contentStyleCount} used styles`;
  }
}

function setTreeView(view) {
  activeTreeView = view;
  const tabs = [
    ['content', '#tab-content', '#content-view'],
    ['headings', '#tab-headings', '#heading-view'],
    ['styles', '#tab-all-styles', '#all-styles-view']
  ];

  for (const [name, tabSelector, panelSelector] of tabs) {
    const selected = view === name;
    $(tabSelector).setAttribute('aria-selected', String(selected));
    $(panelSelector).hidden = !selected;
  }

  if (view === 'headings') {
    const count = renderHeadingOrder($('#heading-order'), report, selectNode);
    $('#style-count').textContent = `${count} headings`;
  } else if (view === 'styles') {
    $('#style-count').textContent = `${report.counts?.['Styles defined'] || 0} styles`;
  } else {
    $('#style-count').textContent = `${contentStyleCount} used styles`;
  }
}

function populateStyleAdder(analysis) {
  const paragraphCount = analysis.paragraphStyleCount || 0;
  const tableCount = analysis.tableStyleCount || 0;
  const available = paragraphCount + tableCount > 0;
  $('#style-adder-panel').hidden = false;
  $('#apply-style').disabled = !available;
  $('#style-adder-status').className = '';
  $('#style-adder-status').textContent = available
    ? `${paragraphCount} paragraph styles and ${tableCount} table styles are available for matching.`
    : 'This document does not define paragraph or table styles to apply.';
}

function showDownload(blob, filename) {
  const url = URL.createObjectURL(blob);
  const link = createElement('a');
  link.href = url;
  link.download = filename;
  document.body.append(link);
  link.click();
  link.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}

function handleTabKeyboard(event) {
  if (!['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
  const tabs = [...event.currentTarget.querySelectorAll('[role="tab"]')];
  const current = tabs.indexOf(event.target.closest('[role="tab"]'));
  if (current < 0) return;

  event.preventDefault();
  const direction = event.key === 'ArrowRight' ? 1 : -1;
  const nextTab = tabs[(current + direction + tabs.length) % tabs.length];
  nextTab.focus();
  const viewByTab = {
    'tab-content': 'content',
    'tab-headings': 'headings',
    'tab-all-styles': 'styles'
  };
  setTreeView(viewByTab[nextTab.id]);
}

function connectTreeControls() {
  $('#style-search').addEventListener('input', drawContentTree);
  $('#expand-all').addEventListener('click', () => {
    document.querySelectorAll('#style-tree details').forEach(item => { item.open = true; });
  });
  $('#collapse-all').addEventListener('click', () => {
    document.querySelectorAll('#style-tree details').forEach(item => { item.open = false; });
  });
  $('#tab-content').addEventListener('click', () => setTreeView('content'));
  $('#tab-headings').addEventListener('click', () => setTreeView('headings'));
  $('#tab-all-styles').addEventListener('click', () => setTreeView('styles'));
  $('[role="tablist"]').addEventListener('keydown', handleTabKeyboard);
}

function connectEvaluation() {
  $('#evaluate-style').addEventListener('click', async () => {
    if (!$('#file').files[0] || !report) return;

    const button = $('#evaluate-style');
    const status = $('#evaluation-status');
    button.disabled = true;
    $('#evaluation-results').hidden = true;
    setStatus(status, 'Sending document text and style details to OpenAI for review…');
    try {
      const result = await evaluateDocument(new FormData($('#upload')));
      renderEvaluation(result, nodesById, (node, nodeId) => {
        setTreeView('content');
        const row = $(`#style-tree [data-node-id="${CSS.escape(nodeId)}"]`);
        selectNode(node, row);
      });
      setStatus(status, 'Evaluation complete. Treat the findings as a review aid.');
    } catch (error) {
      setStatus(status, error.message, true);
    } finally {
      button.disabled = false;
    }
  });
}

function connectStyleAdder() {
  $('#apply-style').addEventListener('click', async () => {
    const file = $('#file').files[0];
    if (!file || !report) return;

    const button = $('#apply-style');
    const status = $('#style-adder-status');
    button.disabled = true;
    setStatus(status, 'Asking OpenAI to assign styles and preparing a copy…');
    try {
      const result = await addDocumentStyles(new FormData($('#upload')));
      showDownload(result.blob, file.name.replace(/\.docx$/i, '-styled.docx'));
      const message = result.assignedCount
        ? `OpenAI added or corrected styles on ${result.assignedCount} item${result.assignedCount === 1 ? '' : 's'}. Your styled copy has downloaded.`
        : 'No style changes were applied. An unchanged copy has downloaded.';
      setStatus(status, message);
    } catch (error) {
      setStatus(status, error.message, true);
    } finally {
      button.disabled = false;
    }
  });
}

function connectUpload() {
  $('#upload').addEventListener('submit', async event => {
    event.preventDefault();
    const form = event.currentTarget;
    const file = $('#file').files[0];
    if (!file) return;

    const submit = $('#submit');
    const status = $('#status');
    submit.disabled = true;
    $('#report').hidden = true;
    setStatus(status, 'Opening document…');

    try {
      report = await inspectDocument(new FormData(form));
      nodesById = collectNodes(report.outline);
      activeNodeId = null;
      $('#filename').textContent = report.filename;
      $('#filesize').textContent = `${(report.size / 1024).toFixed(1)} KB`;
      $('#style-search').value = '';
      drawContentTree();
      renderAllStyles($('#all-styles-list'), report);
      setTreeView('content');
      renderPreview($('#document-preview'), report);
      $('#evaluation-panel').hidden = false;
      $('#evaluation-results').hidden = true;
      $('#evaluation-status').textContent = '';
      populateStyleAdder(report);
      $('#report').hidden = false;
      setStatus(status, 'Document opened. All defined styles are listed in the All styles tab.');
    } catch (error) {
      setStatus(status, error.message, true);
    } finally {
      submit.disabled = false;
    }
  });
}

connectTreeControls();
connectEvaluation();
connectStyleAdder();
connectUpload();
