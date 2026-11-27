# Configuración del Sistema — rediseño Enterprise

Objetivo: rediseñar `SettingsView.xaml` y sus 5 secciones con el sistema visual institucional, sidebar con íconos, tarjetas blancas, inputs/combo sin etiquetas flotantes, toggles en sub-tarjetas, tablas enterprise y pills de auto-guardado, preservando bindings, comandos, validaciones y auto-guardado.
Branch: `V0.15` (rama actual; push/PR a decisión del mantenedor).
Delivery: 1 work unit WPF/XAML + estilos compartidos + ajustes mínimos de estado de auto-guardado; tests de módulo Configuración y suite completa.
Test runner: `dotnet build CommandCenter.slnx -c Release`; `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release`; harness visual temporal 96/120 DPI.

## Specs

- S1. Paleta: `#F8FAFC` lienzo, `#FFFFFF` tarjetas, borde `#E2E8F0` 1px `CornerRadius=12`, input `#CBD5E1` foco `#6D28D9` 1.5px, texto `#0F172A`/`#64748B`, acento `#6D28D9`/hover `#5B21B6`/suave `#F5F3FF`/`#EDE9FE`. Sin paquetes externos. Sin sombras pesadas; solo ambiental `BlurRadius=12`, `Opacity=0.03`.
- S2. Shell: heading `Configuración del Sistema` con badge 42x42 `#EDE9FE` e ícono `#6D28D9`; subtítulo 13 `#64748B`; botón `Menú` secundario blanco/borde `#CBD5E1`/texto `#334155`/corner 8/height 38. Sidebar `Width=248` con separación derecha `#E2E8F0`, 5 ítems con íconos vectoriales 16x16; activo `#F5F3FF`, texto/ícono `#6D28D9`, SemiBold, corner 8 y barra izquierda 3px; hover inactivo `#F1F5F9`. Contenido `MaxWidth=1120`, scrollbar delgado 6 `#CBD5E1` sin flechas.
- S3. Tarjetas: cada una blanca, borde 12, sombra 0.03, cabecera con badge 34x34 `#F5F3FF` e ícono contextual `#6D28D9`, título 16 SemiBold y subtítulo 12.5. Formato y Políticas muestran pill `Auto-guardado` (`#F1F5F9`/`#64748B`) que cambia a `✓ Guardado` (`#DCFCE7`/`#15803D`) al persistir.
- S4. Controles: labels arriba 12 SemiBold `#334155`; inputs/combo 40px, corner 8, reposo `#F8FAFC`, foco blanco/borde `#6D28D9`; ComboBoxItem hover/selección `#EDE9FE`/`#5B21B6`; ToggleSwitch en sub-fila `#F8FAFC`/borde `#E2E8F0`/corner 10/padding 16,12.
- S5. Moneda: Formato en Grid 2 columnas (selector 320 + preview); preview `VISTA PREVIA EN VIVO` con dos mini bloques `Moneda Local`/`Divisa` y divisor. Políticas: selectores USD/Bs.S 240px con labels; toggle piso de margen en sub-fila.
- S6. Pagos: comisiones de adelanto con labels `Transferencia / Pago Móvil` y `Efectivo`, inputs 170px y sufijo `%` interno; botón `Guardar comisiones` alineado, deshabilitado neutro si no editable. Tabla de métodos: borde/cabecera enterprise, filas 48, columnas Orden/Nombre/Tipo/Moneda/Referencia/Activo/Acciones; flechas 26x26 con opacidad 0.3 en primera/última; tipo y moneda badges; activo ToggleSwitch; referencia CheckBox vectorial 18; acciones renombrar/eliminar 28x28. Mantener reglas de comisiones F4 con el mismo estándar.
- S7. Inventario, Red y Sistema: mismas tarjetas, cabeceras con ícono y toggles en sub-fila; tarjetas de servidor/pairing con botones y estado; timezone con label; reinicio conserva alerta ámbar y botón primario.
- S8. Tests: módulo Configuración 100%; build 0 warnings/0 errors; harness 100/125 DPI sin etiquetas sobre bordes, sombras pesadas ni líneas `#000000`.
- S9. Auto-guardado: agregar propiedades de estado `CurrencyFormatAutoSaveStatus` y `PricingPoliciesAutoSaveStatus` (sin cambiar la lógica de persistencia); XAML usa pills con disparadores visuales. Si falla la persistencia, el estado vuelve a `Auto-guardado` y se conserva el error existente.

## Tasks

| ID | Specs | Route | Estado | Commit |
| --- | --- | --- | --- | --- |
| T1 | S1-S9 | inline WPF/XAML + VM status | pendiente | — |
| T2 | S8 | tests/verificación | pendiente | — |

## Log

- L1 (2026-11-26): Pedido del usuario, verbatim: rediseño integral Enterprise de `SettingsView.xaml`, sus subvistas y estilos; sistema de diseño institucional; shell con cabecera/sidebar de 5 secciones con íconos y estado activo; tarjetas y controles DPI-safe sin etiquetas flotantes; rediseño específico de Moneda/Pagos y consistencia en Inventario/Red/Sistema; auto-guardado visible; pruebas y validación 100/125 DPI.

- L2 (2026-11-26): **IMPLEMENTADO**. `SettingsView.xaml` fue reescrito con shell de badge 42x42, botón Menú secundario, sidebar de 248px con 5 íconos vectoriales, estado activo `#F5F3FF`/texto `#6D28D9`/barra 3px, separador derecho y contenido `MaxWidth=1120`. Se creó `SettingsStyles.xaml` con tarjetas blancas/borde 12/sombra 0.03, labels arriba, inputs/combo 40px, ComboBoxItem `#EDE9FE`/`#5B21B6`, toggle sub-card, tablas enterprise, scrollbar 6px sin flechas, botones y checkbox vectorial. Se rediseñaron las 5 secciones: formato/preview en dos bloques, políticas con selectores 240 y toggle piso, comisiones con labels y sufijo %, tabla de métodos con badges/toggle/referencia/acciones, reglas F4, inventario, red y sistema. Se agregaron pills de auto-guardado y se corrigió el formateo de `PaymentMethodDto`/opciones para ComboBox (ToString) y la opacidad first/last de las flechas.
- L3 (2026-11-26): **VERIFICACIÓN**. Build `dotnet build CommandCenter.slnx -c Release`: 0 warnings / 0 errors. Suite .NET completa con PostgreSQL real: 4531/4531. Tests del módulo Settings: 129/129 unitarios + smoke de integración con carril PostgreSQL en la suite completa. Harness WPF 96/120 DPI: 5 secciones renderizadas; sin etiquetas flotantes, sin líneas `#000000`, sin sombras pesadas y sin colisiones de encabezados; tablas y badges visibles. Auto-save pricing verificado en test (`✓ Guardado`/`Auto-guardado`).

- L4 (2026-11-26): **REFINAMIENTO SECCIONES 3-5**. Inventario: pill `Auto-guardado` y aviso ámbar condicionado a `AllowNegativeStock` (`#FFFBEB`/`#FDE68A`/`#B45309`) con el texto de impacto operativo. Red: se eliminó el encapsulado exterior; dos tarjetas blancas iguales lado a lado con grid interior Auto/*/Auto, estado verde/rojo, URL monoespaciada de solo lectura con copia y feedback `✓ Copiado`, recuadro informativo, y botones inferiores alineados. Sistema: badge/subtítulo/pill de auto-guardado, selector de zona horaria 320px con etiqueta superior y preview `HORA ACTUAL EN ZONA SELECCIONADA` en fuente tabular; bloque crítico con borde ámbar izquierdo 4px, badge 38 y botón precautorio ámbar en lugar de morado. Validación: build 0/0; suite .NET 4531/4531; harness 96/120 DPI de las cinco secciones.
