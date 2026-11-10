// Hook de carga (load/resolve) registrado por esbuild-jsx-loader.mjs vía module.register().
// Transpila .jsx y .js con JSX usando esbuild para que node:test pueda montar
// componentes React (renderToString) sin jsdom. En Windows el sourcefile debe ser
// una file:// URL válida (no un path con '/C:/').
import { transformSync } from 'esbuild';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const JSX_TAG_RE = /<\/?[A-Za-z][A-Za-z0-9.-]*(\s|>|\/)/;

// @zxing/library publica CJS (main) y ESM (esm/). Node resolvería el entry CJS desde
// node:test: allí BarcodeFormat/DecodeHintType no son named exports detectables (SyntaxError
// al importar el modal). Vite ya usa el build ESM — alineamos el runner al MISMO build que
// se publica, para que los tests ejerciten exactamente lo que el bundle recibe.
const ZXING_ESM_DIR = new URL('../node_modules/@zxing/library/esm/', import.meta.url).href;
const ZXING_ESM_ENTRY = new URL('index.js', ZXING_ESM_DIR).href;

export async function resolve(specifier, context, nextResolve) {
  if (specifier === '@zxing/library') {
    return { url: ZXING_ESM_ENTRY, shortCircuit: true };
  }

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

  // El arbol ESM de @zxing/library son .js con sintaxis ESM dentro de un paquete sin
  // "type": "module": hay que forzar format module (y las extensiones de sus imports
  // relativos las resuelve el branch de arriba).
  if (url.startsWith(ZXING_ESM_DIR)) {
    return { format: 'module', source: readFileSync(fileURLToPath(url), 'utf8'), shortCircuit: true };
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