namespace PullRequestReleaseNotes
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;

    using LibGit2Sharp;

    using PullRequestReleaseNotes.Models;
    using PullRequestReleaseNotes.Providers;

    public class PullRequestHistoryBuilder
    {
        private readonly ProgramArgs _programArgs;

        private readonly IPullRequestProvider _pullRequestProvider;

        private static readonly Regex ParseSemVer = new Regex(
            @"^[vV]?(?<SemVer>(?<Major>\d+)(\.(?<Minor>\d+))(\.(?<Patch>\d+))?)(\.(?<FourthPart>\d+))?(-(?<Tag>[^\+]*))?(\+(?<BuildMetaData>.*))?$",
            RegexOptions.Compiled);

        public PullRequestHistoryBuilder(ProgramArgs programArgs)
        {
            this._programArgs = programArgs;
            this._pullRequestProvider = programArgs.PullRequestProvider;
        }

        public List<PullRequestDto> BuildHistory()
        {
            var unreleasedCommits = this.GetAllUnreleasedMergeCommits();
            return unreleasedCommits.AsParallel()
                .Select(mergeCommit => this._pullRequestProvider.Get(mergeCommit.Message))
                .Where(pullRequestDto => pullRequestDto != null).ToList();
        }

        private IEnumerable<Commit> GetAllUnreleasedMergeCommits()
        {
            var releasedCommitsHash = new Dictionary<string, Commit>();
            var branchReference = this._programArgs.LocalGitRepository.Branches[this._programArgs.ReleaseBranchRef];

            var tagCommitGroups = this._programArgs.LocalGitRepository.Tags.Where(this.LightOrAnnotatedTags())
                .Where(t => ParseSemVer.Match(t.FriendlyName).Success)
                .Select(
                    t => new
                    {
                        version = t.FriendlyName,
                        commit = t.Target as Commit,
                        versionTag = ParseSemVer.Match(t.FriendlyName).Groups["Tag"].Value.Split('.')[0]
                    }).GroupBy(o => o.versionTag, o => o).ToList();

            var branchTag = this._programArgs.ReleaseBranchVersionTag ?? string.Empty;
            var tagCommits = (tagCommitGroups.SingleOrDefault(g => g.Key == branchTag) ?? tagCommitGroups.First())
                .Select(g => g.commit)
                .Where(x => x != null).ToList();

            var branchAncestors = this._programArgs.LocalGitRepository.Commits
                .QueryBy(new CommitFilter { IncludeReachableFrom = branchReference }).Where(commit => commit.Parents.Count() > 1);
            if (!tagCommits.Any())
            {
                return branchAncestors;
            }

            // for each tagged commit walk down all its parents and collect a dictionary of unique commits
            foreach (var tagCommit in tagCommits)
            {
                // we only care about tags descending from the branch we are interested in
                if (_programArgs.LocalGitRepository.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = branchReference })
                    .Any(c => c.Sha == tagCommit.Sha))
                {
                    var releasedCommits = _programArgs.LocalGitRepository.Commits
                        .QueryBy(new CommitFilter { IncludeReachableFrom = tagCommit.Id })
                        .Where(commit => commit.Parents.Count() > 1)
                        .ToDictionary(i => i.Sha, i => i);
                    releasedCommitsHash.Merge(releasedCommits);
                }
            }

            // remove released commits from the branch ancestor commits as they have been previously released
            return branchAncestors.Except(releasedCommitsHash.Values.AsEnumerable());
        }

        /// <summary>
        ///     Check if is a light or annotated tags.
        /// </summary>
        /// <returns>
        ///     The <c>true</c> if is a annotated tag.
        /// </returns>
        private Func<Tag, bool> LightOrAnnotatedTags()
        {
            if (this._programArgs.GitTagsAnnotated)
            {
                return t => t.IsAnnotated;
            }

            return t => true;
        }
    }
}