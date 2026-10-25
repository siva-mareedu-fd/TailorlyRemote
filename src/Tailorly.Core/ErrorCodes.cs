namespace Tailorly.Core;

/// <summary>Stable machine-readable error codes (ApiDesign §10). Never rename a value.</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string BadRequest = "BAD_REQUEST";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string CsrfInvalid = "CSRF_INVALID";
    public const string EmailNotVerified = "EMAIL_NOT_VERIFIED";
    public const string ConsentRequired = "CONSENT_REQUIRED";
    public const string AccountDisabled = "ACCOUNT_DISABLED";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string InviteCodeRequired = "INVITE_CODE_REQUIRED";
    public const string InviteCodeInvalid = "INVITE_CODE_INVALID";
    public const string UnsupportedFileType = "UNSUPPORTED_FILE_TYPE";
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string FileUnsafe = "FILE_UNSAFE";
    public const string ResumeRequired = "RESUME_REQUIRED";
    public const string JobDescriptionRequired = "JOB_DESCRIPTION_REQUIRED";
    public const string AnalysisRequired = "ANALYSIS_REQUIRED";
    public const string NoAcceptedEdits = "NO_ACCEPTED_EDITS";
    public const string StaleEdit = "STALE_EDIT";
    public const string BudgetExceeded = "BUDGET_EXCEEDED";
    public const string RunLimitReached = "RUN_LIMIT_REACHED";
    public const string RateLimited = "RATE_LIMITED";
    public const string ConcurrentUpdate = "CONCURRENT_UPDATE";
    public const string InvalidBoardUrl = "INVALID_BOARD_URL";
    public const string BoardNotFound = "BOARD_NOT_FOUND";
    public const string WatchlistLimit = "WATCHLIST_LIMIT";
    public const string FormatUnavailable = "FORMAT_UNAVAILABLE";
    public const string TermsNotReviewed = "TERMS_NOT_REVIEWED";
    public const string AiUnavailable = "AI_UNAVAILABLE";

    /// <summary>Resource error code: the model's reply failed validation twice (after one repair). Not in ApiDesign §10 yet (DECISIONS.md).</summary>
    public const string AiInvalidOutput = "AI_INVALID_OUTPUT";
    public const string Timeout = "TIMEOUT";
    public const string InternalError = "INTERNAL_ERROR";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
}
