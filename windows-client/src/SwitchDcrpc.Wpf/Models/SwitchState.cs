using System.Text.Json.Serialization;

namespace SwitchDcrpc.Wpf.Models;

public sealed class SwitchState
{
    [JsonPropertyName("firmware")]
    public string? Firmware { get; set; }

    [JsonPropertyName("active_program_id")]
    public string? ActiveProgramId { get; set; }

    [JsonPropertyName("active_game")]
    public string? ActiveGame { get; set; }

    [JsonPropertyName("started_sec")]
    public ulong StartedSec { get; set; }

    [JsonPropertyName("last_update_sec")]
    public ulong LastUpdateSec { get; set; }
}
