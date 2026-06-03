// The agent-neutral GitHub layer was extracted to the Agent.GitHub library (Step-37).
// Surfacing it as a global using keeps the per-file using churn out of the ~30 consumer files
// that reference its types (PullRequest, ReviewVerdict, GitHubOptions, IGitHubProjectsClient, …).
global using Agent.GitHub;
