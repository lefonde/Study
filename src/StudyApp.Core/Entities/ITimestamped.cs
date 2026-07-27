namespace StudyApp.Core.Entities;

/// <summary>Entities whose CreatedAt/UpdatedAt are maintained automatically by the persistence layer.</summary>
public interface ITimestamped
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}
