// Loader ESM para node:test — transpila JSX (y JS que contenga JSX) con esbuild.
// Resuelve 8.4-N5: permite tests de MONTAJE REAL de componentes React sin jsdom
// (renderToString) que antes eran imposibles con `node --test` puro.
// Se carga con `node --import ./test/esbuild-jsx-loader.mjs --test ...` y se
// auto-registra vía module.register() (patrón oficial de Node >= 20.6).
import { register } from 'node:module';

// register() acepta file:// URLs (en Windows un path crudo rompe el hook de carga).
register(new URL('./esbuild-jsx-loader-hook.mjs', import.meta.url));