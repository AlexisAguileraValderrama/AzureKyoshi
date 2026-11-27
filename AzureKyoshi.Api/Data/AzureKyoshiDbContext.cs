using AzureKyoshi.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AzureKyoshi.Api.Data;

public sealed class AzureKyoshiDbContext(DbContextOptions<AzureKyoshiDbContext> options)
    : DbContext(options)
{
    public DbSet<StudyFolder> StudyFolders => Set<StudyFolder>();
    public DbSet<StudyModule> StudyModules => Set<StudyModule>();
    public DbSet<StudyQuiz> StudyQuizzes => Set<StudyQuiz>();
    public DbSet<QuizQuestion> QuizQuestions => Set<QuizQuestion>();
    public DbSet<QuizAttempt> QuizAttempts => Set<QuizAttempt>();
    public DbSet<QaMessage> QaMessages => Set<QaMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<StudyFolder>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.Property(f => f.OwnerUserId).HasMaxLength(128).IsRequired();
            entity.Property(f => f.Name).HasMaxLength(80).IsRequired();
            entity.Property(f => f.ColorHex).HasMaxLength(20).IsRequired();
            entity.Property(f => f.CertificationCode).HasMaxLength(20);
            entity.HasIndex(f => new { f.OwnerUserId, f.Name });
        });

        modelBuilder.Entity<StudyModule>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.OwnerUserId).HasMaxLength(128).IsRequired();
            entity.Property(m => m.Title).HasMaxLength(250).IsRequired();
            entity.Property(m => m.RequestedTopic).HasMaxLength(250).IsRequired();
            entity.Property(m => m.CertificationCode).HasMaxLength(20);
            entity.Property(m => m.SummaryMarkdown).IsRequired();
            entity.Property(m => m.SourceContent).IsRequired();
            entity.Property(m => m.SourcesJson).IsRequired();

            entity.HasIndex(m => new { m.OwnerUserId, m.CreatedAtUtc });

            entity.HasOne(m => m.Folder)
                .WithMany(f => f.Modules)
                .HasForeignKey(m => m.FolderId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<StudyQuiz>(entity =>
        {
            entity.HasKey(q => q.Id);
            entity.Property(q => q.Title).HasMaxLength(200).IsRequired();
            entity.Property(q => q.Difficulty).HasMaxLength(30).IsRequired();

            entity.HasOne(q => q.StudyModule)
                .WithMany(m => m.Quizzes)
                .HasForeignKey(q => q.StudyModuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QuizQuestion>(entity =>
        {
            entity.HasKey(q => q.Id);
            entity.Property(q => q.QuestionText).IsRequired();
            entity.Property(q => q.OptionsJson).IsRequired();
            entity.Property(q => q.Explanation).IsRequired();

            entity.HasOne(q => q.StudyQuiz)
                .WithMany(qz => qz.Questions)
                .HasForeignKey(q => q.StudyQuizId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QuizAttempt>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.UserId).HasMaxLength(128).IsRequired();
            entity.Property(a => a.SelectedOptionIndicesJson).HasMaxLength(1000).IsRequired();

            entity.HasOne(a => a.StudyQuiz)
                .WithMany(qz => qz.Attempts)
                .HasForeignKey(a => a.StudyQuizId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QaMessage>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Role).HasMaxLength(20).IsRequired();
            entity.Property(m => m.Content).IsRequired();
            entity.HasIndex(m => new { m.StudyModuleId, m.CreatedAtUtc });

            entity.HasOne(m => m.StudyModule)
                .WithMany(sm => sm.QaMessages)
                .HasForeignKey(m => m.StudyModuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
