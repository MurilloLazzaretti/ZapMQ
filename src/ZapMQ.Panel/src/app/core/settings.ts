import { HttpErrorResponse } from '@angular/common/http';

/**
 * The text of a settings file as strict JSON: without the comments and the commas left before
 * a closing bracket, which the applications that read these files accept and the browser does not.
 */
export function relaxed(text: string): string {
  let out = '';
  for (let at = 0; at < text.length; at++) {
    const char = text[at];
    if (char === '"') {
      // A string goes as it is, whatever it has inside.
      const start = at;
      for (at++; at < text.length && text[at] !== '"'; at++) {
        if (text[at] === '\\') {
          at++;
        }
      }
      out += text.slice(start, at + 1);
    } else if (char === '/' && text[at + 1] === '/') {
      while (at < text.length && text[at] !== '\n') {
        at++;
      }
      out += '\n';
    } else if (char === '/' && text[at + 1] === '*') {
      const end = text.indexOf('*/', at + 2);
      // The lines stay where they were, so that a mistake is still said at its own line.
      out += text.slice(at, end < 0 ? text.length : end + 2).replace(/[^\n]/g, ' ');
      at = end < 0 ? text.length : end + 1;
    } else {
      out += char;
    }
  }
  // A comma with nothing after it but the closing bracket, outside of any string; blanked, not removed, for the same reason.
  let strict = '';
  for (let at = 0; at < out.length; at++) {
    if (out[at] === '"') {
      const start = at;
      for (at++; at < out.length && out[at] !== '"'; at++) {
        if (out[at] === '\\') {
          at++;
        }
      }
      strict += out.slice(start, at + 1);
    } else {
      strict += out[at] === ',' && /^\s*[}\]]/.test(out.slice(at + 1)) ? ' ' : out[at];
    }
  }
  return strict;
}

/** What is wrong with a JSON settings file, as the applications read it; nothing when it reads. */
export function jsonProblem(text: string): string {
  try {
    JSON.parse(relaxed(text));
    return '';
  } catch (error) {
    return (error as Error).message;
  }
}

/** Why a file that was sent did not get there, in words that say what to do about it. */
export function uploadProblem(failure: unknown, otherwise: string): string {
  const response = failure as HttpErrorResponse;
  if (response.status === 413) {
    return 'O arquivo é maior do que o proxy reverso deixa enviar. Aumente client_max_body_size na configuração do NGINX (Processos e serviços → Proxy reverso) e envie de novo.';
  }
  return response.error?.error ?? otherwise;
}
