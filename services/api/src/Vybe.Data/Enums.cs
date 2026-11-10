namespace Vybe.Data;

// Stored as snake_case text with a CHECK constraint (see TextEnum). Adding a value is an
// expand-contract change: add it here, deploy the migration, then start writing it.

public enum PlayerKind { Guest, Registered }

public enum PlayerStatus { Active, Suspended, Deleting, Deleted }

/// <summary>Spec §17 decision 6: who can see the profile. Default <c>players</c> (signed-in players).</summary>
public enum ProfileVisibility { Players, Private }

/// <summary>Spec §17 decision 1: the self-declared age band of the age gate (the birth year is never stored).</summary>
public enum AgeBand { Adult }

public enum ItemSlot { Hair, Top, Bottom, Shoes, Accessory, Emote }

public enum Rarity { Common, Rare, Epic }

public enum InventorySource { Purchase, StarterGrant, AchievementGrant, AdminGrant }

public enum LedgerReason { GameReward, QuestReward, Purchase, LevelReward, ChallengeReward, AdminAdjustment }

public enum GameSessionStatus { Created, Active, Submitted, Verified, Rejected, Expired }

public enum SessionRejectionReason
{
    NotStarted,
    RulesetMismatch,
    InvalidEvent,
    OutOfRange,
    TooManyEvents,
    NonMonotonic,
    CadenceViolation,
    SubmittedTooEarly,
}

public enum ChallengeStatus { Open, Accepted, Completed, Expired, Cancelled }

public enum QuestPeriod { Daily, Once }

public enum QuestStatus { Active, Completed, Claimed }

public enum ReportReason { OffensiveName, Cheating, Harassment, Other }

public enum ReportStatus { Open, Actioned, Dismissed }

public enum AdminRole { Moderator, Administrator }

public enum ExportStatus { Queued, Running, Ready, Failed }

public enum DeletionStatus { Queued, Processing, Completed }

public enum OutboxStatus { Pending, Done, Failed }

public enum IdempotencyStatus { InProgress, Completed }
