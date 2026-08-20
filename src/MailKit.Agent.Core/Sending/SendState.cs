using System.Text.Json.Serialization;
using MailKit.Agent.Core.Serialization;

namespace MailKit.Agent.Core.Sending;

[JsonConverter(typeof(LowerSnakeCaseEnumConverter<SendState>))]
public enum SendState
{
    Prepared,
    Attempting,
    Succeeded,
    Failed,
    Indeterminate,
    /// <summary>
    /// Terminal state for the drafts send mode: the prepared message was appended
    /// to the account's Drafts folder and nothing was delivered. The record is
    /// never re-invoked; sending the draft is the human's manual act.
    /// </summary>
    Drafted
}
