using System.Text;

namespace SarnautCore.UI.Tests;

internal static class UiProductFixture
{
    public const string Json = """
        {
          "schema_id": "sarnaut.ui-product/v2",
          "catalogs": {
            "cursors": "ui/cursor_catalog.tres",
            "sounds": "ui/sound_catalog.tres",
            "theme": "ui/ui_theme.tres"
          },
          "screens": [
            {
              "id": "login",
              "scene": "ui/LoginAccount.ui.tscn",
              "initially_visible": false,
              "cues": { "show": "ui_menu_open", "hide": "ui_menu_close" },
              "roles": [
                { "id": "account", "node": "LoginPanel/Account", "initially_visible": true, "cursor": "default" },
                { "id": "password", "node": "LoginPanel/Password", "initially_visible": true, "cursor": "default" },
                {
                  "id": "enter",
                  "node": "LoginPanel/Enter",
                  "initially_visible": true,
                  "cursor": "use",
                  "cues": { "hover": "button_yes", "press": "button_press" }
                },
                {
                  "id": "options",
                  "node": "Bottom/Options",
                  "initially_visible": true,
                  "cursor": "default",
                  "cues": { "show": "button_yes", "hide": "button_no" }
                },
                {
                  "id": "local",
                  "node": "Bottom/Local",
                  "initially_visible": false,
                  "cues": { "show": "ui_menu_open" }
                },
                { "id": "saved-row", "node": "SavedAccounts/Row", "initially_visible": true }
              ],
              "actions": [
                {
                  "id": "submit-login",
                  "arguments": [],
                  "triggers": [
                    { "role": "enter", "event": "pressed" },
                    { "role": "account", "event": "submitted" },
                    { "role": "password", "event": "submitted" }
                  ]
                },
                {
                  "id": "toggle-options",
                  "arguments": [],
                  "triggers": [{ "role": "options", "event": "toggled" }]
                }
              ],
              "values": [
                { "id": "account-name", "role": "account", "kind": "text", "access": "read-write", "secret": false },
                { "id": "account-password", "role": "password", "kind": "text", "access": "write", "secret": true }
              ],
              "collections": [
                { "id": "saved-accounts", "role": "account", "item_role": "saved-row", "item_scene": "ui/SavedAccountRow.tscn", "selection": "single" }
              ],
              "buttons": [
                {
                  "role": "enter",
                  "toggle": false,
                  "initial_variant": "standard",
                  "variants": [
                    { "id": "standard", "visual_state": "primary", "cues": { "press": "button_yes" }, "inputs": [{ "input": "primary-released", "event": "pressed" }] }
                  ]
                },
                {
                  "role": "options",
                  "toggle": true,
                  "initial_variant": "open",
                  "variants": [
                    { "id": "open", "visual_state": "options-open", "cues": { "show": "bag_open", "hide": "bag_close" }, "inputs": [{ "input": "primary-released", "event": "toggled" }] },
                    { "id": "closed", "visual_state": "options-closed", "cues": { "press": "button_no" }, "inputs": [{ "input": "primary-released", "event": "toggled" }] }
                  ]
                },
                {
                  "role": "saved-row",
                  "toggle": true,
                  "initial_variant": "clear",
                  "variants": [
                    { "id": "clear", "visual_state": "clear", "inputs": [] },
                    { "id": "selected", "visual_state": "selected", "inputs": [] }
                  ]
                }
              ],
              "selection_groups": [],
              "focus_order": ["account", "password", "enter", "options", "local"]
            }
          ]
        }
        """;

    public const string InteractionJson = """
        {
          "schema_id": "sarnaut.ui-product/v2",
          "catalogs": {
            "cursors": "catalogs/cursors.tres",
            "sounds": "catalogs/sounds.tres",
            "theme": "ui_theme.tres"
          },
          "screens": [
            {
              "id": "selector",
              "scene": "screens/selector.tscn",
              "initially_visible": true,
              "roles": [
                { "id": "screen-input", "node": ".", "initially_visible": true },
                { "id": "preview", "node": "Preview", "initially_visible": true, "cues": { "hover": "preview_hover" } },
                { "id": "choice-a", "node": "Choices/A", "initially_visible": true },
                { "id": "choice-b", "node": "Choices/B", "initially_visible": true },
                { "id": "open", "node": "Open", "initially_visible": true, "cues": { "hover": "row_hover", "press": "row_press" } }
              ],
              "actions": [
                { "id": "preview", "arguments": [{ "name": "identity", "kind": "product-id", "value": "league-warrior" }], "triggers": [{ "role": "preview", "event": "hover-entered" }] },
                { "id": "preview-end", "arguments": [], "triggers": [{ "role": "preview", "event": "hover-exited" }] },
                { "id": "begin-preview-drag", "arguments": [], "triggers": [{ "role": "preview", "event": "primary-pressed" }] },
                { "id": "open", "arguments": [{ "name": "character", "kind": "collection-item-id", "collection": "characters" }], "triggers": [{ "role": "open", "event": "double-pressed" }] },
                { "id": "preview-row", "arguments": [{ "name": "character", "kind": "collection-item-id", "collection": "characters" }], "triggers": [{ "role": "open", "event": "hover-entered" }] },
                { "id": "select", "arguments": [{ "name": "character", "kind": "collection-item-id", "collection": "characters" }], "triggers": [{ "role": "open", "event": "toggled" }] },
                { "id": "select", "arguments": [{ "name": "identity", "kind": "product-id", "value": "choice-a" }], "triggers": [{ "role": "choice-a", "event": "toggled" }] },
                { "id": "select", "arguments": [{ "name": "identity", "kind": "product-id", "value": "choice-b" }], "triggers": [{ "role": "choice-b", "event": "toggled" }] },
                { "id": "previous", "arguments": [], "triggers": [{ "role": "screen-input", "event": "navigate-previous" }] }
              ],
              "values": [],
              "collections": [
                { "id": "characters", "role": "preview", "item_role": "open", "item_scene": "items/character.tscn", "selection": "single" }
              ],
              "buttons": [
                { "role": "choice-a", "toggle": true, "initial_variant": "clear", "variants": [{ "id": "clear", "visual_state": "clear", "inputs": [{ "input": "primary-released", "event": "toggled" }] }, { "id": "selected", "visual_state": "selected", "inputs": [{ "input": "primary-released", "event": "toggled" }] }] },
                { "role": "choice-b", "toggle": true, "initial_variant": "clear", "variants": [{ "id": "clear", "visual_state": "clear", "inputs": [{ "input": "primary-released", "event": "toggled" }] }, { "id": "selected", "visual_state": "selected", "inputs": [{ "input": "primary-released", "event": "toggled" }] }] },
                { "role": "open", "toggle": true, "initial_variant": "clear", "variants": [{ "id": "clear", "visual_state": "clear", "inputs": [{ "input": "primary-released", "event": "toggled" }, { "input": "double-pressed", "event": "double-pressed" }, { "input": "hover-entered", "event": "hover-entered" }] }, { "id": "selected", "visual_state": "selected", "inputs": [{ "input": "double-pressed", "event": "double-pressed" }, { "input": "hover-entered", "event": "hover-entered" }] }] }
              ],
              "selection_groups": [
                { "id": "choice", "roles": ["choice-a", "choice-b"], "allow_empty": true, "initial_role": "choice-a" }
              ],
              "focus_order": ["choice-a", "choice-b", "open"]
            }
          ]
        }
        """;

    public static UiProductManifest Parse(string json = Json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return NativeUiProductManifestParser.Parse(stream);
    }
}
