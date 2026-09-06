# Logistics.Module

Este módulo forma parte de la arquitectura modular de Soluciones POS (`CommandCenter.slnx`) y está reservado para la expansión de capacidades de despacho, seguimiento de repartos y logística de entregas a domicilio (Delivery).

## Estado y Alcance
- **Estado**: Módulo planificado y desacoplado para Fase de Entregas.
- **Entidades de Dominio enlazadas**:
  - `Core.Entities.UserRole.Driver`: Rol de conductor/repartidor para autenticación y despacho.
  - `Core.Entities.DeliveryStatus`: Estados de ciclo de vida de entregas (`Pending`, `InTransit`, `Delivered`, `Failed`).
- **Puntos de Integración Futuros**:
  - Gestión de rutas y asignación de pedidos a conductores.
  - Monitoreo geográfico y confirmación digital de entrega (firma/código QR).
  - Integración desacoplada con `Sales.Module` mediante eventos de dominio (`SaleDispatchedEvent`, `SaleDeliveredEvent`).
