async function readJson(response, fallbackMessage) {
  try {
    return await response.json();
  } catch {
    throw new Error(fallbackMessage);
  }
}

async function postForm(url, formData) {
  const response = await fetch(url, { method: 'POST', body: formData });
  return response;
}

export async function inspectDocument(formData) {
  const response = await postForm('/api/inspect', formData);
  const data = await readJson(response, 'The server could not inspect this document.');
  if (!response.ok) throw new Error(data.error || 'Document inspection failed.');
  return data;
}

export async function evaluateDocument(formData) {
  const response = await postForm('/api/evaluate-style', formData);
  const data = await readJson(response, 'The style evaluation service returned an unreadable response.');
  if (!response.ok) throw new Error(data.error || 'Style evaluation failed.');
  return data;
}

export async function addDocumentStyles(formData) {
  const response = await postForm('/api/add-styles', formData);
  if (!response.ok) {
    const data = await readJson(response, 'The style service returned an unreadable response.');
    throw new Error(data.error || 'Could not add styles to this document.');
  }

  return {
    blob: await response.blob(),
    assignedCount: Number(response.headers.get('X-Styles-Assigned') || 0)
  };
}
