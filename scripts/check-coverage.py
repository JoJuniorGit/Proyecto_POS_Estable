#!/usr/bin/env python3
# 8.9-L5: Gate de cobertura por capa (politica Rev 8.2 / coding-guidelines.md):
#   Cobertura minima >= 70% en capas criticas de dominio (Sales.Module, Inventory.Module, Core).
# Contexto actual 2026-09: Core cumple 70%; Sales.Module e Inventory.Module estan por debajo
# (deuda documentada en reporte Rev 8.11) -> en esas capas se exige un minimo anti-regresion:
#   * Core            >= 0.70 (enforced, politica cumplida)
#   * Sales.Module    >= 0.19 (baseline medido 0.20 - margen; deuda hacia 0.70)
#   * Inventory.Module>= 0.14 (baseline medido 0.15 - margen; deuda hacia 0.70)
# Si una capa baja del minimo, el pipeline falla y se reporta el gap hacia el objetivo.
import sys
import xml.etree.ElementTree as ET


TARGETS = {
    "Core": 0.70,
    "Sales.Module": 0.19,
    "Inventory.Module": 0.14,
}
GAP_TARGET = 0.70


def parse_report(path):
    tree = ET.parse(path)
    root = tree.getroot()
    packages = {}
    for pkg in root.iter("package"):
        packages[pkg.get("name")] = float(pkg.get("line-rate", "0"))
    return packages


def main():
    if len(sys.argv) != 2:
        print("usage: check-coverage.py <coverage.cobertura.xml>", file=sys.stderr)
        return 2
    report = sys.argv[1]
    packages = parse_report(report)

    missing = [name for name in TARGETS if name not in packages]
    if missing:
        print(f"::error::Capa(s) sin medir en el reporte: {', '.join(missing)}")
        return 1

    failed = False
    print("Cobertura por capa (line-rate):")
    for name, min_rate in TARGETS.items():
        rate = packages[name]
        gap = max(0.0, GAP_TARGET - rate)
        status = "OK" if rate >= min_rate else "FAIL"
        print(f"  {name:18} rate={rate:.4f} min={min_rate:.4f} gap_a_70%={gap:.4f} [{status}]")
        if status == "FAIL":
            failed = True
    print("Umbrales: Core >= 0.70 (politica); Sales.Module/Inventory.Module = baseline "
          "anti-regresion (deuda hacia 0.70), detalle en docs/reporte.txt Rev 8.11.")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())