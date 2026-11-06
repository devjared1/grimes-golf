using System.Xml;
using System.Xml.Linq;

namespace GrimesGolf.Core.Course;

/// <summary>
/// Publishes a real course's shape under another name. A map file says a lot about where it's from (the club's name, its
/// website and phone number, hole and pond names), so this keeps only the tags <see cref="OsmCourse"/> plays from, then
/// gives the course's outline the new name. Nothing about how the course plays changes: its fingerprint stays the same.
/// </summary>
public static class OsmAnonymiser
{
    /// <summary>Every tag the importer reads, apart from names. New tags it starts to read need adding here.</summary>
    private static readonly HashSet<string> Used = new()
    {
        "golf", "leisure", "natural", "landuse", "waterway", "width", "ref", "par", "type", "highway", "golf_cart", "area", "bridge",
    };

    /// <param name="mappedName">The course's name in the map: its outline is the one that gets <paramref name="publicName"/>.</param>
    /// <exception cref="InvalidDataException">No golf course outline in the map has that name.</exception>
    public static void Rewrite(string mapFile, string mappedName, string publicName)
    {
        XDocument doc;
        using (var reader = XmlReader.Create(mapFile, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc = XDocument.Load(reader);

        int outlines = 0;
        foreach (var element in (doc.Root?.Elements() ?? Enumerable.Empty<XElement>()).Where(e => e.Name.LocalName is "way" or "relation"))
        {
            var tags = element.Elements("tag").ToList();
            string? Value(string key) => (string?)tags.FirstOrDefault(t => (string?)t.Attribute("k") == key)?.Attribute("v");
            bool ours = Value("leisure") == "golf_course" && Value("name")?.Contains(mappedName, StringComparison.OrdinalIgnoreCase) == true;
            foreach (var tag in tags.Where(t => !Used.Contains((string?)t.Attribute("k") ?? ""))) tag.Remove();
            if (!ours) continue;
            outlines++;
            element.Add(new XElement("tag", new XAttribute("k", "name"), new XAttribute("v", publicName)));
        }
        if (outlines == 0) throw new InvalidDataException($"No golf course outline in the map is named '{mappedName}'.");

        using var writer = XmlWriter.Create(mapFile, new XmlWriterSettings { Indent = true, Encoding = new System.Text.UTF8Encoding(false) });
        doc.Save(writer);
    }
}
