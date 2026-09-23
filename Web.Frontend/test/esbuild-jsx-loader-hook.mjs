// Hook de carga (load/resolve) registrado por esbuild-jsx-loader.mjs vía module.register().
// Transpila .jsx y .js con JSX usando esbuild para que node:test pueda montar
// componentes React (renderToString) sin jsdom. En Windows el sourcefile debe ser
// una file:// URL válida (no un path con '/C:/').
import { transformSync } from 'esbuild';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const JSX_TAG_RE = /<\/?[A-Za-z][A-Za-z0-9.-]*(\s|>|\/)/;

export async function resolve(specifier, context, nextResolve) {
  // Imports sin extensión (estilo Vite): './PaymentForm', '../../context/CartContext', etc.
  if (specifier.startsWith('.') && !/\.[a-zA-Z0-9]+$/.test(specifier)) {
    const parentUrl = context.parentURL;
    if (parentUrl && parentUrl.startsWith('file:')) {
      const baseUrl = new URL(specifier, parentUrl);
      const candidates = [
        baseUrl.href,
        baseUrl.href + '.js',
        baseUrl.href + '.jsx',
        new URL(specifier + '/index.js', parentUrl).href,
        new URL(specifier + '/index.jsx', parentUrl).href,
      ];
      for (const candidate of candidates) {
        try {
          return await nextResolve(candidate, context);
        } catch {
          // probar el siguiente candidato
        }
      }
    }
  }
  return nextResolve(specifier, context);
}

export async function load(url, context, nextLoad) {
  if (!url.startsWith('file:')) {
    return nextLoad(url, context);
  }

  const pathname = fileURLToPath(url);
  const isJsx = pathname.endsWith('.jsx');
  const isJs = pathname.endsWith('.js') || pathname.endsWith('.mjs');

  // CSS imports son side-effect en Vite; en node:test se resuelven como modulo vacio
  // para permitir el montaje de componentes que importan su hoja de estilos.
  if (pathname.endsWith('.css')) {
    return { format: 'module', source: '', shortCircuit: true };
  }

  if (!isJsx && !isJs) {
    return nextLoad(url, context);
  }

  const source = readFileSync(pathname, 'utf8');

  // Los .js puros se dejan nativos salvo que contengan JSX (p. ej. un *.test.js de
  // montaje). .jsx siempre pasa por esbuild.
  if (isJs && !JSX_TAG_RE.test(source)) {
    return nextLoad(url, context);
  }

  const { code } = transformSync(source, {
    loader: 'jsx',
    jsx: 'automatic',
    format: 'esm',
    sourcefile: url, // file:// URL — válida también en Windows
    target: 'node20',
  });

  return { format: 'module', source: code, shortCircuit: true };
}