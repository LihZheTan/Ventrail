using System.Collections.Generic;
using Ventrail.Models;

namespace Ventrail.Services.Contracts;

public interface IRepositoryManager
{
    string RepoPath { get; }
    List<CommitNode> CommitHistory { get; }

    bool InitRepository(string path);
    void Stage(string filePath);
    bool Commit(string message, string author);
    List<CommitNode> GetCommitHistory();
}