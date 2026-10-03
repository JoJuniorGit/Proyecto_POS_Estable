using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace Desktop.Client.Services;

public partial class SupplierInvoiceService
{
    private static List<string[]> ReadXmlRows(Stream content, CancellationToken cancellationToken)
    {
        using var reader = XmlReader.Create(content, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        cancellationToken.ThrowIfCancellationRequested();
        var elements = document.Root?.DescendantsAndSelf().ToList() ?? [];
        var repeatedRecords = elements
            .Where(HasXmlFields)
            .GroupBy(element => element.Parent)
            .SelectMany(siblings => siblings.GroupBy(element => element.Name.LocalName))
            .Where(group => group.Count() > 1)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => ScoreXmlRecord(group.First()))
            .FirstOrDefault();

        List<XElement> records;
        if (repeatedRecords is not null && ScoreXmlRecord(repeatedRecords.First()) > 0)
        {
            records = repeatedRecords.ToList();
        }
        else
        {
            var candidate = elements
                .Where(HasXmlFields)
                .Select(element => new { Element = element, Score = ScoreXmlRecord(element) })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .FirstOrDefault();
            records = candidate is null ? [] : [candidate.Element];
        }

        if (records.Count == 0)
        {
            return [];
        }

        var headers = records
            .SelectMany(GetXmlFieldNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rows = new List<string[]> { headers };
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = GetXmlFieldValues(record);
            rows.Add(headers.Select(header => values.GetValueOrDefault(header, string.Empty)).ToArray());
        }

        return rows;
    }

    private static bool HasXmlFields(XElement element) =>
        element.Elements().Any() || element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration);

    private static int ScoreXmlRecord(XElement element)
    {
        var names = GetXmlFieldNames(element).ToArray();
        if (names.Length == 0)
        {
            return 0;
        }

        var score = names.Length;
        foreach (var name in names)
        {
            if (LooksLikeInvoiceColumn(name))
            {
                score += 10;
            }
        }

        score += element.Elements().Count(child => !child.HasElements) * 2;
        score -= element.Elements().Count(child => child.HasElements) * 10;
        return score;
    }

    private static IEnumerable<string> GetXmlFieldNames(XElement element)
    {
        foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
        {
            yield return attribute.Name.LocalName;
        }

        foreach (var child in element.Elements())
        {
            yield return child.Name.LocalName;
        }
    }

    private static Dictionary<string, string> GetXmlFieldValues(XElement element)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
        {
            values[attribute.Name.LocalName] = attribute.Value.Trim();
        }

        foreach (var child in element.Elements())
        {
            values[child.Name.LocalName] = child.Value.Trim();
        }

        return values;
    }

    private static bool LooksLikeInvoiceColumn(string header)
    {
        var normalized = NormalizeHeader(header);
        return normalized.Contains("barcode", StringComparison.Ordinal) ||
               normalized.Contains("ean", StringComparison.Ordinal) ||
               normalized.Contains("suppliercode", StringComparison.Ordinal) ||
               normalized.Contains("productcode", StringComparison.Ordinal) ||
               normalized.Contains("name", StringComparison.Ordinal) ||
               normalized.Contains("producto", StringComparison.Ordinal) ||
               normalized.Contains("quantity", StringComparison.Ordinal) ||
               normalized.Contains("cantidad", StringComparison.Ordinal) ||
               normalized.Contains("unitcost", StringComparison.Ordinal) ||
               normalized.Contains("cost", StringComparison.Ordinal) ||
               normalized.Contains("costo", StringComparison.Ordinal);
    }
}
