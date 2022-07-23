using LibGit2Sharp;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;

string rootRepoPath = args[0];
string baseCommit = args[1];

var patches = GetPatches( rootRepoPath, baseCommit );

if ( patches.All( p => !p.Patch.Any() )) {
  Console.WriteLine( "No changes at all ..." );
  return 0;
}

var solution = ProjectModelTasks.ParseSolution(@"C:\git\mono-repo\mono.sln");

var changedFolders = patches
  .SelectMany( p => p.Patch.Where( c => c.Mode != Mode.GitLink ).Select( f => (AbsolutePath) Path.Combine( p.WorkingDirectory, Path.GetDirectoryName( f.Path )! ) ) )
  .Distinct()
  .ToArray();

var toAdd = new Stack<AbsolutePath>();

foreach (var changedFolder in changedFolders)
{
  if (changedFolder == solution.Path!.Parent )
  {
    continue;
  }

  // TODO: What do we want to do with changes to file not inside projects?
  foreach (var a in solution.AllProjects.Where(k => k.Path.Parent.Contains(changedFolder)))
  {
    toAdd.Push(a.Path);
  }
}

var affected = new HashSet<AbsolutePath>();

var projectToUsers = CreateUsedByMap(solution);

while (toAdd.Count > 0)
{
  var a = toAdd.Pop();
  if (affected.Contains(a))
  {
    continue;
  }

  affected.Add(a);
  foreach (var dep in projectToUsers[a])
  {
    toAdd.Push(dep);
  }
}

Console.WriteLine("Total affected:");

foreach (var a in affected)
{
  Console.WriteLine(a);
}

return 0;

Dictionary<AbsolutePath, HashSet<AbsolutePath>> CreateUsedByMap( Solution sln ) {
  HashSet<AbsolutePath> GetProjectReferences( Project p ) {
    var project = p.GetMSBuildProject();
    var paths = project.AllEvaluatedItems.Where( i => i.ItemType == "ProjectReference" )
      .Select( pr => p.Path.Parent / pr.EvaluatedInclude )
      .ToArray();
    return new HashSet<AbsolutePath>( paths );
  }

  var dictionary = sln.AllProjects.ToDictionary( p => p.Path, p => new HashSet<AbsolutePath>() );

  // TODO: We might also want to check for items with paths pointing above the project root
  // e.g. a shared test binary file or similar

  var projectDeps = sln.AllProjects.AsParallel().Select( p => ( project: p, usedBy: GetProjectReferences( p ) ) ).ToArray();

  foreach ( var entry in projectDeps ) {
    foreach ( var dep in entry.usedBy ) {
      dictionary[dep].Add( entry.project.Path );
    }
  }

  return dictionary;
}

static List<(string WorkingDirectory, Patch Patch)> GetPatches( string rootRepoPath, string rootBaseCommit ) {
  var patches = new List<(string WorkingDirectory, Patch Patch)>();

  var rootRepo = new Repository( rootRepoPath );

  var rootPatch = rootRepo.Diff.Compare<Patch>(
    rootRepo.Commits.Single( c => c.Sha == rootBaseCommit ).Tree,
    rootRepo.Head.Tip.Tree );

  patches.Add( ( rootRepoPath, rootPatch ) );

  // This does not go recursive, but we don't have recursive submodules
  foreach ( var gitLink in rootPatch.Where( f => f.Mode == Mode.GitLink ) ) {
    var submodule = rootRepo.Submodules.FirstOrDefault( m => gitLink.Path == m.Path );
    if ( submodule == null ) {
      throw new InvalidOperationException( "Could not find submodule matching " + gitLink.Path );
    }

    var headOid = gitLink.Oid;
    var baseOid = gitLink.OldOid;

    var subRepoPath = Path.Combine( rootRepoPath, submodule.Path );
    var subRepo = new Repository( subRepoPath );
    var subPatch = subRepo.Diff.Compare<Patch>(
      subRepo.Commits.Single( c => c.Sha == baseOid.Sha ).Tree,
      subRepo.Commits.Single( c => c.Sha == headOid.Sha ).Tree );

    patches.Add( ( subRepoPath, subPatch ) );
  }

  return patches;
}
