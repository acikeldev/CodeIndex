using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CodeIndex.Models;

using TypeInfo = CodeIndex.Models.TypeInfo;

namespace CodeIndex.Parsing;

/// <summary>
/// Parses a XAML file into a <see cref="SourceFileIndex"/> (<see cref="Language.Xaml"/>) so the code-behind's
/// XAML half is visible to the C# tools:
/// <list type="bullet">
/// <item>the <c>x:Class</c> type as a partial <see cref="TypeInfo"/> (base type = root element, e.g. Window) whose
/// members are the <c>x:Name</c> / <c>Name</c> fields the XAML compiler generates, typed by their element — so a
/// receiver like <c>saveButton.Focus()</c> or <c>scalePanel.Reset()</c> resolves;</item>
/// <item>identifier-valued attributes as <see cref="XamlHook"/>s (event wirings), so a handler wired only in XAML
/// has a caller.</item>
/// </list>
/// Names inside templates (DataTemplate, ControlTemplate, …) are skipped: they generate no field. A file that is not
/// well-formed XML is still indexed (empty) so text search and references keep covering it.
/// </summary>
public static partial class XamlParser
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string ClrNamespacePrefix = "clr-namespace:";

    // Common non-event properties whose values are bare identifiers (enum values, content) — dropped at parse time
    // to keep the hook list small. Correctness never depends on this list: a hook only counts when its value names
    // a method on the x:Class type.
    private static readonly HashSet<string> NonEventAttributes = new(StringComparer.Ordinal)
    {
        "Name", "Content", "Text", "Header", "Title", "ToolTip", "Tag", "Value", "Orientation", "HorizontalAlignment",
        "VerticalAlignment", "HorizontalContentAlignment", "VerticalContentAlignment", "Visibility", "Stretch",
        "TextWrapping", "TextAlignment", "TextTrimming", "FontWeight", "FontStyle", "FontFamily", "Dock",
        "DockPanel.Dock", "WindowState", "WindowStyle", "ResizeMode", "WindowStartupLocation", "SizeToContent",
        "SelectionMode", "SelectionUnit", "ScrollViewer.VerticalScrollBarVisibility",
        "ScrollViewer.HorizontalScrollBarVisibility", "VerticalScrollBarVisibility", "HorizontalScrollBarVisibility",
        "Placement", "Mode", "UpdateSourceTrigger", "Background", "Foreground", "BorderBrush", "Fill", "Stroke",
        "Cursor", "FlowDirection", "ClipToBounds", "SnapsToDevicePixels", "Focusable", "IsEnabled", "IsReadOnly",
        "IsChecked", "AutoGenerateColumns", "GridLinesVisibility", "HeadersVisibility", "Property", "TargetType",
        "TargetName", "Key", "Uid", "Language", "StaysOpen", "AllowsTransparency", "ShowInTaskbar", "Topmost",
    };

    public static SourceFileIndex Parse(string path, string projectName, string text)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return Empty(path, projectName);
        }

        XElement? root = doc.Root;
        string? xClass = root?.Attribute(XName.Get("Class", XamlNamespace))?.Value;
        if (root is null || string.IsNullOrWhiteSpace(xClass))
        {
            // ResourceDictionary, App resources, styles: no code-behind type, but still text-indexed.
            return Empty(path, projectName);
        }

        int dot = xClass.LastIndexOf('.');
        string? ns = dot > 0 ? xClass[..dot] : null;
        string className = dot > 0 ? xClass[(dot + 1)..] : xClass;

        List<MemberInfo> fields = new();
        List<XamlHook> hooks = new();
        foreach (XElement e in root.DescendantsAndSelf())
        {
            string local = e.Name.LocalName;
            if (local.Contains('.', StringComparison.Ordinal))
            {
                continue; // property element (<Grid.RowDefinitions>), not an object
            }

            int line = ((IXmlLineInfo)e).HasLineInfo() ? ((IXmlLineInfo)e).LineNumber : 1;
            bool inTemplate = e.Ancestors().Any(a => a.Name.LocalName.EndsWith("Template", StringComparison.Ordinal));
            string? name = e.Attribute(XName.Get("Name", XamlNamespace))?.Value ?? e.Attribute("Name")?.Value;
            if (e != root && !inTemplate && name is not null && Identifier().IsMatch(name))
            {
                string fieldType = QualifiedType(e);
                fields.Add(new MemberInfo
                {
                    Name = name,
                    Kind = Models.SymbolKind.Field,
                    ReturnType = fieldType,
                    Signature = $"{fieldType} {name}",
                    StartLine = line,
                    LineCount = 1,
                });
            }

            foreach (XAttribute a in e.Attributes())
            {
                if (a.IsNamespaceDeclaration || a.Name.NamespaceName.Length > 0 || NonEventAttributes.Contains(a.Name.LocalName)
                    || !Identifier().IsMatch(a.Value))
                {
                    continue;
                }

                hooks.Add(new XamlHook { Element = local, Attribute = a.Name.LocalName, Member = a.Value, Line = line });
            }
        }

        int lineCount = text.Count(c => c == '\n') + 1;
        TypeInfo type = new()
        {
            Name = className,
            Kind = Models.SymbolKind.Class,
            TypeKeyword = "partial class",
            BaseTypes = [root.Name.LocalName],
            StartLine = 1,
            LineCount = lineCount,
            IsNested = false,
            Namespace = ns,
            Members = fields,
        };

        return new SourceFileIndex
        {
            FileName = Path.GetFileName(path),
            SourceFilePath = path,
            ProjectName = projectName,
            Namespace = ns,
            Language = Language.Xaml,
            Types = [type],
            XamlHooks = hooks.Count > 0 ? hooks : null,
        };
    }

    private static SourceFileIndex Empty(string path, string projectName) => new()
    {
        FileName = Path.GetFileName(path),
        SourceFilePath = path,
        ProjectName = projectName,
        Language = Language.Xaml,
    };

    // "pos:ScalePanel" with xmlns:pos="clr-namespace:App.Controls" → "App.Controls.ScalePanel"; default/WPF
    // namespaces → the bare element name ("Button").
    private static string QualifiedType(XElement e)
    {
        string ns = e.Name.NamespaceName;
        if (ns.StartsWith(ClrNamespacePrefix, StringComparison.Ordinal))
        {
            string clr = ns[ClrNamespacePrefix.Length..];
            int semi = clr.IndexOf(';', StringComparison.Ordinal);
            clr = semi >= 0 ? clr[..semi] : clr;
            return clr.Length > 0 ? $"{clr}.{e.Name.LocalName}" : e.Name.LocalName;
        }

        return e.Name.LocalName;
    }

    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{Nd}_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
