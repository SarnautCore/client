// Ported from ao-godot-converter runtime templates (Apache-2.0).
using Godot;

[GlobalClass]
public partial class AllodsFmodProject : Resource
{
    [Export] public string source_path { get; set; } = string.Empty;
    [Export] public string[] event_names { get; set; } = [];
    [Export] public string[] bank_files { get; set; } = [];
    [Export] public string[] sample_files { get; set; } = [];
    [Export(PropertyHint.MultilineText)] public string event_bank_map_json { get; set; } = string.Empty;
    [Export] public string fev_file { get; set; } = string.Empty;
}
