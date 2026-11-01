export const $ = (selector, root = document) => root.querySelector(selector);

export function createElement(tag, text = null, className = '') {
  const element = document.createElement(tag);
  if (text !== null) element.textContent = text;
  if (className) element.className = className;
  return element;
}

export function setStatus(element, message, isError = false) {
  element.textContent = message;
  element.classList.toggle('error', isError);
}
