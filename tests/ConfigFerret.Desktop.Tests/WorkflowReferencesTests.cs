using ConfigFerret.Desktop.Services;
using ConfigFerret.GitHub;

namespace ConfigFerret.Desktop.Tests;

public class WorkflowReferencesTests
{
    [Theory]
    [InlineData("  key: ${{ secrets.DEPLOY_KEY }}", GitHubVariableKind.Secret, "DEPLOY_KEY")]
    [InlineData("  publish-profile: ${{secrets.deploy_key}}", GitHubVariableKind.Secret, "DEPLOY_KEY")]
    [InlineData("  - run: curl -H \"x: ${{ secrets['DEPLOY_KEY'] }}\"", GitHubVariableKind.Secret, "DEPLOY_KEY")]
    [InlineData("  app-name: ${{ vars.APP_NAME }}-${{ github.ref_name }}", GitHubVariableKind.Variable, "APP_NAME")]
    [InlineData("  if: vars.ENABLED == 'true' && secrets.TOKEN != ''", GitHubVariableKind.Variable, "ENABLED")]
    [InlineData("  if: vars.ENABLED == 'true' && secrets.TOKEN != ''", GitHubVariableKind.Secret, "TOKEN")]
    [InlineData("  name: ${{ format('{0}', vars[\"APP_NAME\"]) }}", GitHubVariableKind.Variable, "APP_NAME")]
    public void Finds_a_reference_anywhere_in_a_line(string line, GitHubVariableKind kind, string name) =>
        Assert.True(WorkflowReferences.FromText(line).Uses(kind, name));

    [Theory]
    [InlineData("  # key: ${{ secrets.DEPLOY_KEY }}", GitHubVariableKind.Secret, "DEPLOY_KEY")]
    [InlineData("  key: ${{ vars.DEPLOY_KEY }}", GitHubVariableKind.Secret, "DEPLOY_KEY")]
    [InlineData("  key: ${{ inputs.my_secrets.DEPLOY_KEY }}", GitHubVariableKind.Secret, "DEPLOY_KEY")]
    [InlineData("  key: ${{ needs.build.outputs.vars.DEPLOY_KEY }}", GitHubVariableKind.Variable, "DEPLOY_KEY")]
    [InlineData("  secrets: inherit", GitHubVariableKind.Secret, "inherit")]
    public void Does_not_count_comments_other_kinds_or_other_contexts(string line, GitHubVariableKind kind, string name) =>
        Assert.False(WorkflowReferences.FromText(line).Uses(kind, name));

    [Fact]
    public void Skips_a_file_that_cannot_be_read_and_reads_the_rest()
    {
        using var repo = new TempFolder();
        repo.Write("deploy.yml", "env:\n  A: ${{ vars.ONE }}\n");

        var references = WorkflowReferences.Read([repo.Full("missing.yml"), repo.Full("deploy.yml")]);

        Assert.True(references.Uses(GitHubVariableKind.Variable, "ONE"));
    }
}
