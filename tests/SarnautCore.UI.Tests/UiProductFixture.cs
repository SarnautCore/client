using System.Text;

namespace SarnautCore.UI.Tests;

internal static class UiProductFixture
{
    public const string Json = """
        {
          "schema_id": "sarnaut.ui-product/v1",
          "catalogs": {
            "cursors": "ui/cursor_catalog.tres",
            "sounds": "ui/sound_catalog.tres"
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
                }
              ],
              "actions": [
                {
                  "id": "submit-login",
                  "triggers": [
                    { "role": "enter", "event": "pressed" },
                    { "role": "account", "event": "submitted" },
                    { "role": "password", "event": "submitted" }
                  ]
                },
                {
                  "id": "toggle-options",
                  "triggers": [{ "role": "options", "event": "toggled" }]
                }
              ],
              "values": [
                { "id": "account-name", "role": "account", "kind": "text", "access": "read-write", "secret": false },
                { "id": "account-password", "role": "password", "kind": "text", "access": "write", "secret": true }
              ],
              "collections": [
                { "id": "saved-accounts", "role": "account", "item_scene": "ui/SavedAccountRow.tscn", "selection": "single" }
              ],
              "buttons": [
                {
                  "role": "enter",
                  "toggle": false,
                  "initial_variant": "standard",
                  "variants": [
                    { "id": "standard", "visual_state": "primary", "cues": { "press": "button_yes" } }
                  ]
                },
                {
                  "role": "options",
                  "toggle": true,
                  "initial_variant": "open",
                  "variants": [
                    { "id": "open", "visual_state": "options-open", "cues": { "show": "bag_open", "hide": "bag_close" } },
                    { "id": "closed", "visual_state": "options-closed", "cues": { "press": "button_no" } }
                  ]
                }
              ],
              "focus_order": ["account", "password", "enter", "options", "local"]
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
