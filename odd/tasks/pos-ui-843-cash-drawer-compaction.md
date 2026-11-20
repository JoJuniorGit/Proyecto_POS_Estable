# POS-UI-843 — Compactación y pulido visual de Caja de Tienda (WPF)

Objetivo: eliminar el scroll global de `CashDrawerView`, compactar los KPIs por moneda, corregir contraste/jerarquía de encabezados, modernizar tarjetas e iconos, dar estado activo a las pestañas y normalizar las mini-tarjetas de últimos ingresos. Sin cambios de lógica ni de bindings de datos.
Componente: `Desktop.Client (WPF) — CashDrawerView`.
Prioridad: P1.

## Specs

- **S1 — Sin scroll global.** Header, KPIs y pestañas en filas Auto; la subsección activa ocupa `*` con scroll interno. La grilla de movimientos debe ser visible al abrir en 1366x768/1920x1080.
- **S2 — Encabezados por moneda.** `Bs.S · MONEDA LOCAL` en píldora índigo #EDE9FE/#5B21B6; `USD · DIVISA` en píldora pizarra #E2E8F0/#1E293B. Sin badge LOCAL ilegible.
- **S3 — Tarjetas KPI.** Fondo blanco, borde 1px, sombra sutil (BlurRadius 8/Opacidad 0.04), acento izquierdo violeta/verde/rojo; padding y tipografía compactos; icono del banco en badge 36x36 #F1F5F9/#475569.
- **S4 — Pestañas.** Segmented control con píldora blanca elevada y texto #6D28D9 para la activa; inactiva #64748B; sin foco punteado nativo.
- **S5 — Últimos ingresos.** Mini-tarjetas de ancho uniforme (210px), título con ellipsis y ToolTip; panel colapsado por defecto; botón de colapso sin `FocusVisualStyle`.
- **S6 — Sin cambios de negocio.** Cero cambios en comandos, endpoints, autorización, reglas de caja o bindings de datos; build Release 0/0.

## Tasks

- T1 | S1/S3 | wpf | Quitar `ScrollViewer` global, reorganizar filas y compactar KPIs. | completed
- T2 | S2/S4 | wpf | Píldoras de moneda y segmented control con estado activo. | completed
- T3 | S3/S5 | wpf | Sombras, bordes, iconos, tarjetas de últimos ingresos y foco. | completed
- T4 | S6 | verify | Build Release, tests focalizados y ANEXO. | in progress — inspección visual pendiente

## Log

- L1 (2026-11-15): pedido del mantenedor, ticket **POS-UI-843 — [WPF][UI/UX] Compactación vertical, refinamiento de tarjetas KPI por moneda y estilizado de pestañas en Caja de Tienda**. Diagnóstico: tabla principal bajo el pliegue por scroll global; LOCAL ilegible; sombras oscuras e iconos sin contenedor; pestañas sin estado activo; tarjetas de últimos ingresos con desborde y foco punteado. Criterios: grilla visible sin scroll global, contraste, ancho homogéneo y 0 cambios de lógica/bindings.
- L2 (2026-11-15): **T1-T3 COMPLETADAS (work unit local, sin commit)**. `CashDrawerView` sin ScrollViewer global, fila `*` para la sección activa y scroll interno; KPIs compactos; píldoras de moneda; segmented control; tarjetas KPI con borde/acento/sombra sutil; icono del banco en badge; mini-tarjetas de 210px con ellipsis/ToolTip; foco del toggle suprimido. Evidencia: build WPF Release 0/0; suite focalizada de caja 22/22. Pendiente: inspección visual manual y commit/push.
