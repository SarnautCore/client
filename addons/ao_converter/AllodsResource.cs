// Ported from ao-godot-converter runtime templates (Apache-2.0).
using Godot;

[GlobalClass]
public partial class AllodsResource : Resource
{
    [Export] public string source_path { get; set; } = string.Empty;
    [Export] public string source_class { get; set; } = string.Empty;
    [Export] public string root_element { get; set; } = string.Empty;
    [Export(PropertyHint.MultilineText)] public string raw_xml { get; set; } = string.Empty;
    [Export] public string[] references { get; set; } = [];
    [Export] public string[] companion_files { get; set; } = [];
    [Export(PropertyHint.MultilineText)] public string xml_parse_error { get; set; } = string.Empty;
}
