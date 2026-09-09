#!/usr/bin/env python3
# 8.9-L5: Gate de cobertura por capa (politica Rev 8.2 / coding-guidelines.md):
#   Cobertura minima >= 70% en capas criticas de dominio (Sales.Module, Inventory.Module, Core).
# 8.26-E4 (decision): el gate computa la tasa de DOMINIO sobre el reporte cobertura, excluyendo
# el scaffolding generado por dotnet-ef (*.Migrations.*): ese codigo se verifica por EJECUCION
# real via smoke MigratedSchema / MigrateAsync, no aporta senial de cobertura de dominio.
# Baselines 8.26-E4 medidos sobre codigo de dominio (con los tests de convergencia nuevos):
#   * Core            >= 0.70 (enforced, politica cumplida)
#   * Sales.Module    >= 0.80 (baseline ~0.85 - margen 5pp)
#   * Inventory.Module>= 0.72 (baseline ~0.76 - margen 4pp)
# Estos umbrales reemplazan los anti-regresion 0.19/0.14 de Rev 8.11 (deuda hacia 0.70 saldada).
# Si una capa baja del minimo, el pipeline falla y se reporta el gap hacia el objetivo.
import sys
import xml.etree.ElementTree as ET


TARGETS = {
    "Core": 0.70,
    "Sales.Module": 0.80,
    "Inventory.Module": 0.72,
}
GAP_TARGET = 0.70
EXCLUDED_NAMESPACE = ".Migrations."


def parse_domain_line_rates(path):
    """Tasa de lineas por paquete, ignorando clases bajo el namespace *.Migrations.*."""
    tree = ET.parse(path)
    root = tree.getroot()
    rates = {}
    for pkg in root.iter("package"):
        mod = pkg.get("name").split(",")[0]
        if mod not in TARGETS:
            continue
        valid = covered = 0
        for cls in pkg.iter("class"):
            if EXCLUDED_NAMESPACE in cls.get("name"):
                continue
            for line in cls.findall("lines/line"):
                valid += 1
                if int(line.get("hits", "0")) > 0:
                    covered += 1
        rates[mod] = (covered / valid) if valid else 0.0
    return rates


def main():
    if len(sys.argv) != 2:
        print("usage: check-coverage.py <coverage.cobertura.xml>", file=sys.stderr)
        return 2
    report = sys.argv[1]
    rates = parse_domain_line_rates(report)

    missing = [name for name in TARGETS if name not in rates]
    if missing:
        print(f"::error::Capa(s) sin medir en el reporte: {', '.join(missing)}")
        return 1

    failed = False
    print("Cobertura de dominio por capa (line-rate, excluye *.Migrations.*):")
    for name, min_rate in TARGETS.items():
        rate = rates[name]
        gap = max(0.0, GAP_TARGET - rate)
        status = "OK" if rate >= min_rate else "FAIL"
        print(f"  {name:18} rate={rate:.4f} min={min_rate:.4f} gap_a_70%={gap:.4f} [{status}]")
        if status == "FAIL":
            failed = True
    print("Umbrales: Core >= 0.70 (politica); Sales.Module >= 0.80 e Inventory.Module >= 0.72 "
          "(baselines 8.26-E4, deuda saldada). Detalle en docs/reporte.txt Rev 8.26.")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())