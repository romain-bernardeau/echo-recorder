namespace Echo.App;

/// <summary>
/// Last-known Mistral API health, driving the discreet sidebar indicator (green dot =
/// key present and no standing error; red button = key missing or last API call failed,
/// clicking opens the API help tab). STICKY by design: an error stays until a call or a
/// key test succeeds, so the user gets a stable "something is wrong, here is help" signal
/// instead of a blinking one. Written from the pipeline and the key test; read by get_state.
/// </summary>
internal static class ApiHealth
{
    private static string? _message;

    public static bool HasError => _message is not null;
    public static string? Message => _message;

    /// <summary>A call to the Mistral API succeeded (or the key tested valid).</summary>
    public static void Ok() => _message = null;

    /// <summary>A call to the Mistral API failed; keep the user-facing French message.</summary>
    public static void Report(string? userMessage)
        => _message = string.IsNullOrWhiteSpace(userMessage) ? "erreur API Mistral" : userMessage!.Trim();
}
