// 8.7-L6: copia de texto al portapapeles sin depender de document.execCommand como vía principal
// (API Clipboard moderna + fallback legacy marcado como último recurso para WebView/CORS antiguos).
export async function copyTextToClipboard(text) {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // Sin permisos o contexto HTTP; continuar con el fallback de selección.
  }
  return legacyCopyFallback(text);
}

function legacyCopyFallback(text) {
  const textarea = document.createElement('textarea');
  textarea.value = text;
  textarea.setAttribute('readonly', '');
  textarea.style.position = 'absolute';
  textarea.style.left = '-9999px';
  textarea.style.top = '-9999px';
  document.body.appendChild(textarea);
  let copied = false;
  try {
    textarea.select();
    textarea.setSelectionRange(0, textarea.value.length);
    copied = document.execCommand('copy');
  } catch {
    copied = false;
  } finally {
    document.body.removeChild(textarea);
  }
  return copied;
}