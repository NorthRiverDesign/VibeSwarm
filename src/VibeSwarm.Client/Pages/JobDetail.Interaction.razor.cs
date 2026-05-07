using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Client.Pages;

public partial class JobDetail : ComponentBase
{
    // Interaction response state
    private bool _isSubmittingResponse = false;
    private string? _interactionError = null;
    private List<string>? _interactionChoices = null;

    #region Interaction

    private async Task SubmitInteractionResponse(string response)
    {
        if (_isSubmittingResponse) return;

        _isSubmittingResponse = true;
        _interactionError = null;
        StateHasChanged();

        try
        {
            var trimmedResponse = response.Trim();
            var keepSubmittingState = false;
            if (_hubConnection != null && _hubConnection.State == HubConnectionState.Connected)
            {
                var success = await _hubConnection.InvokeAsync<bool>("SubmitInteractionResponse", JobId.ToString(), trimmedResponse);
                if (!success)
                {
                    _interactionError = "Failed to deliver response. The job may have already completed or been cancelled.";
                }
                else if (Job != null)
                {
                    var submittedAt = DateTime.UtcNow;
                    _pendingSessionMessages.Add(new JobMessage
                    {
                        Id = Guid.NewGuid(),
                        JobId = Job.Id,
                        Role = MessageRole.User,
                        Content = trimmedResponse,
                        CreatedAt = submittedAt,
                        Source = MessageSource.User,
                        Level = MessageLevel.Normal
                    });

                    Job.CurrentActivity = "Submitting interaction response...";
                    Job.LastActivityAt = submittedAt;
                    keepSubmittingState = true;
                }
            }
            else
            {
                _interactionError = "Connection to server not available. Please refresh the page.";
            }

            if (!keepSubmittingState)
            {
                _isSubmittingResponse = false;
            }
        }
        catch (Exception)
        {
            _interactionError = "Failed to send response. Please try again.";
            _isSubmittingResponse = false;
        }
        finally
        {
            StateHasChanged();
        }
    }

    private void ReconcilePendingSessionMessages()
    {
        if (Job == null || _pendingSessionMessages.Count == 0)
        {
            return;
        }

        _pendingSessionMessages.RemoveAll(pendingMessage => Job.Messages.Any(existingMessage =>
            existingMessage.Role == pendingMessage.Role
            && string.Equals(existingMessage.Content, pendingMessage.Content, StringComparison.Ordinal)
            && Math.Abs((existingMessage.CreatedAt - pendingMessage.CreatedAt).TotalMinutes) <= 30));
    }

    #endregion
}
