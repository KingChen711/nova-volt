namespace Nvm.Quality.Entities;

/// <summary>Trạng thái chất lượng của unit, độc lập với execution và vị trí vật lý (scope §6.2).</summary>
public enum QualityState { Pending, Released, Held, Rework, Scrapped }

public static class QualityReasonCodes
{
    public const string QualityHold = "QUALITY_HOLD";
    public const string Scrapped = "SCRAPPED";
    public const string DuplicateSerial = "DUPLICATE_SERIAL";
    public const string HoldNotFound = "HOLD_NOT_FOUND";
    public const string HoldNotActive = "HOLD_NOT_ACTIVE";
    public const string InvalidTarget = "INVALID_HOLD_TARGET";
    public const string SeparationOfDuties = "SEPARATION_OF_DUTIES";
    public const string MissingSignature = "MISSING_SIGNATURE";
    public const string DuplicateSigner = "DUPLICATE_SIGNER";
    public const string SignatureWrongSubject = "SIGNATURE_WRONG_SUBJECT";
    public const string SignatureStaleContent = "SIGNATURE_STALE_CONTENT";
    public const string SignatureNotApproval = "SIGNATURE_NOT_APPROVAL";
    public const string SignatureNotFound = "SIGNATURE_NOT_FOUND";
    public const string ReauthenticationFailed = "REAUTHENTICATION_FAILED";
    public const string RoleNotHeld = "ROLE_NOT_HELD";
    public const string NcrNotFound = "NCR_NOT_FOUND";
    public const string NcrClosed = "NCR_CLOSED";
    public const string CascadeNotReady = "CASCADE_NOT_READY";
}
