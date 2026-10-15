using mEmuera.Contracts;
using System.Text;

namespace mEmuera.Runtime.Execution;

public sealed partial class GameSession
{
    private bool inputMacroEnabled = true, messageSkip;
    private string? sequenceInput;
    private readonly Queue<string> macroInputs = new();
    bool IBuiltinContext.InputMacroEnabled { get => inputMacroEnabled; set => inputMacroEnabled = value; }
    bool IBuiltinContext.MessageSkip { get => messageSkip; set => messageSkip = value; }
    void IBuiltinContext.SequenceInput(string text) => sequenceInput = text;
    private void QueueInputMacro(string text)
    {
        var segments = inputMacroEnabled ? InputMacro.Expand(text) : [text];
        macroInputs.Clear();
        foreach (string segment in segments) macroInputs.Enqueue(segment);
    }
    private void AdvanceInputMacro()
    {
        if (state != StopReason.WaitingInput || input is null || input.IgnoreInput) return;
        if (input.SequenceInputAllowed && sequenceInput is { } queued)
        {
            sequenceInput = null;
            QueueInputMacro(queued);
        }
        if (messageSkip && input.Kind == InputKind.Wait) { Accept(new(0L), "macro"); return; }
        messageSkip = false;
        while (macroInputs.TryDequeue(out string? text))
        {
            if (inputMacroEnabled && text.Contains("\\e", StringComparison.Ordinal))
            {
                text = text.Replace("\\e", "", StringComparison.Ordinal);
                messageSkip = true;
            }
            var applied = Apply(new InputReply(epoch, input.Id, text, null, clock(), "macro"));
            if (applied.Accepted || input is null) return;
        }
        messageSkip = false;
    }
    private static string FirstInputCharacter(string text) => text.Length == 0 ? text : text.EnumerateRunes().First().ToString();
}
