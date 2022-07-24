using LibGit2Sharp;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;

var rootSln = (AbsolutePath) args[0];
string baseCommit = args[1];

var patches = Patches( rootSln.Parent, baseCommit );

if ( patches.All( p => !p.Patch.Any() ) ) {
  Console.WriteLine( "No changes at all ..." );
  return 0;
}

var solution = ProjectModelTasks.ParseSolution( rootSln );

var changedPaths = patches
  .SelectMany(
    p => p.Patch.Where( c => c.Mode != Mode.GitLink )
      //.Select( f => (AbsolutePath) Path.Combine( p.WorkingDirectory, Path.GetDirectoryName( f.Path )! ) ) )
      .Select( f => (AbsolutePath) Path.Combine( p.WorkingDirectory, f.Path ) )
      .Distinct() )
  .ToArray();

var toAdd = new Stack<AbsolutePath>();

foreach ( var changedPath in changedPaths ) {
  if ( Equals( changedPath, solution.Path!.Parent ) ) {
    continue;
  }

  toAdd.Push( changedPath );

  // Source code items are typically implicit in modern csproj files
  // So any changes for which a project sits at the root we add the project file itself to the change set
  // because anyone depending on that project is impact, as well as anyone referencing the changed file directly.
  foreach ( var a in solution.AllProjects.Where( k => k.Path.Parent.Contains( changedPath ) ) ) {
    toAdd.Push( a.Path );
  }
}

var affected = new HashSet<AbsolutePath>();

var fileToUsers = CreateUsedByMap( solution );

while ( toAdd.Count > 0 ) {
  var a = toAdd.Pop();
  if ( affected.Contains( a ) ) {
    continue;
  }

  affected.Add( a );
  if ( fileToUsers.TryGetValue( a, out var users ) ) {
    foreach ( var dep in users ) {
      toAdd.Push( dep );
    }
  }
}

Console.WriteLine( "Total affected:" );

foreach ( var a in affected ) {
  Console.WriteLine( a );
}

return 0;

Dictionary<AbsolutePath, HashSet<AbsolutePath>> CreateUsedByMap( Solution sln ) {
  HashSet<AbsolutePath> ReferencedFiles( Project p ) {
    var project = p.GetMSBuildProject();

    var itemPaths = project.AllEvaluatedItems.Select(
        pr => Path.IsPathRooted( pr.EvaluatedInclude )
          ? (AbsolutePath) pr.EvaluatedInclude
          : p.Path.Parent / pr.EvaluatedInclude )
      .Where( f => File.Exists( f ) || Directory.Exists( f ) )
      .ToArray();

    var importPaths = project.Imports.Select( i => i.ImportedProject.ProjectFileLocation.File )
      .Where( f => p.Solution.Path!.Parent.Contains( f ) ).Select( f => (AbsolutePath)f )
      .ToArray();
    return new HashSet<AbsolutePath>( itemPaths.Concat( importPaths ) );
  }

  var dictionary = sln.AllProjects.ToDictionary( p => p.Path, _ => new HashSet<AbsolutePath>() );

  var fileUsages = sln.AllProjects.AsParallel()
    .Select( p => ( project: p, usedBy: ReferencedFiles( p ) ) )
    .ToArray();

  foreach ( var entry in fileUsages ) {
    foreach ( var dep in entry.usedBy ) {
      if ( !dictionary.ContainsKey( dep ) ) {
        dictionary[dep] = new HashSet<AbsolutePath>();
      }
      dictionary[dep].Add( entry.project.Path );
    }
  }

  return dictionary;
}

static List<(string WorkingDirectory, Patch Patch)> Patches( string rootRepoPath, string rootBaseCommit ) {
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
