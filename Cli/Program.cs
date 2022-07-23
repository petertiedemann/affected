// See https://aka.ms/new-console-template for more information

using LibGit2Sharp;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;

Dictionary<AbsolutePath, HashSet<AbsolutePath>> CreateUsedByMap(Solution sln)
{
  HashSet<AbsolutePath> GetProjectReferences(Project p)
  {
    var project = p.GetMSBuildProject();
    var paths = project.AllEvaluatedItems.Where(i => i.ItemType == "ProjectReference")
      .Select(pr => p.Path.Parent / pr.EvaluatedInclude)
      .ToArray();
    return new HashSet<AbsolutePath>(paths);
  }

  {
    var dictionary = sln.AllProjects.ToDictionary(p => p.Path, p => new HashSet<AbsolutePath>());

    foreach (var project in sln.AllProjects)
    {
      var deps = GetProjectReferences(project);

      foreach (var dep in deps)
      {
        dictionary[dep].Add(project.Path);
      }
    }

    return dictionary;
  }
}

var repo = new Repository(@"C:\git\core");

var head = repo.Commits.First();
var prev = repo.Commits.Skip(10).First();

// var patch = repo.Diff.Compare<Patch>(head.Tree, prev.Tree);

var patch = repo.Diff.Compare<Patch>(head.Tree, DiffTargets.WorkingDirectory);

var solution = ProjectModelTasks.ParseSolution(@"C:\git\core\Core.sln");

var changedFolders = patch.Select(p => solution.Path!.Parent / Path.GetDirectoryName(p.Path)).Distinct().ToArray();

var projectToUsers = CreateUsedByMap(solution);

Stack<AbsolutePath> toAdd = new Stack<AbsolutePath>();
foreach (var changedFolder in changedFolders)
{
  if (changedFolder == solution.Path!.Parent)
  {
    continue;
  }

  foreach (var a in solution.AllProjects.Where(k => k.Path.Parent.Contains(changedFolder)))
  {
    toAdd.Push(a.Path);
  }
}

HashSet<AbsolutePath> affected = new HashSet<AbsolutePath>();

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