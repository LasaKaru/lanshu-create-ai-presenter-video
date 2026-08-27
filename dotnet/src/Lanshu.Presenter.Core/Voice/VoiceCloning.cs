using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Voice;

public sealed record ClonedVoice(string VoiceId, string Name, string Provider);

/// <summary>
/// Creates a provider-side voice from an authorized sample. Implemented only by engines that
/// actually support it; the router checks for this interface before offering to clone.
/// </summary>
public interface IVoiceCloner
{
    /// <summary>Creates the voice and returns its id. The caller has already checked authorization.</summary>
    Task<ClonedVoice> CloneAsync(string name, string samplePath, CancellationToken cancellationToken = default);

    /// <summary>Removes a voice previously created by <see cref="CloneAsync"/>.</summary>
    Task DeleteAsync(string voiceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The authorization a clone requires, checked in one place so no caller can skip a step.
/// A voice is a person's likeness: uploading a sample and minting a synthetic copy of it needs
/// the sample owner's permission and the operator's approval to upload, both recorded on the job.
/// </summary>
public sealed record CloneEligibility(bool Allowed, string Reason)
{
    public static CloneEligibility Evaluate(
        string voiceSamplePath,
        bool voiceCloneApproved,
        bool remoteUploadApproved,
        bool alreadyHaveVoiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceSamplePath))
        {
            return new CloneEligibility(false, "no voice sample was supplied");
        }

        if (!File.Exists(FileSystemUtil.ExpandPath(voiceSamplePath)))
        {
            return new CloneEligibility(false, "the voice sample file is missing");
        }

        if (!voiceCloneApproved)
        {
            return new CloneEligibility(false, "voice_clone_approved is not recorded on this job");
        }

        if (!remoteUploadApproved)
        {
            return new CloneEligibility(false, "remote_upload_approved is not recorded on this job");
        }

        if (alreadyHaveVoiceId)
        {
            return new CloneEligibility(false, "this job already has a voice id");
        }

        return new CloneEligibility(true, "authorized");
    }
}

public sealed class VoiceCloneException : Exception
{
    public VoiceCloneException(string message) : base(message)
    {
    }
}
