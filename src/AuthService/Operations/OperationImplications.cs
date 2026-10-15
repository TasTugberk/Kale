namespace AuthService.Operations;

/// <summary>
/// Works with [Implies(...)] links between operations, e.g. "billing.InvoiceManage" implies "billing.InvoiceRead".
/// <paramref name="implies"/> maps each operation to the operations it directly implies.
/// </summary>
public static class OperationImplications
{
    /// <summary>
    /// Everything a role effectively holds: its granted operations plus everything they imply, directly or
    /// through a chain. Endpoints can then declare only the lowest operation they need.
    /// </summary>
    public static IReadOnlySet<string> Expand(IEnumerable<string> granted, ILookup<string, string> implies)
    {
        var effective = new HashSet<string>();
        var toVisit = new Stack<string>(granted);

        while (toVisit.Count > 0)
        {
            var operation = toVisit.Pop();

            // Add returns false for an operation we already have. Skipping it stops cycles from looping forever.
            if (!effective.Add(operation))
            {
                continue;
            }

            foreach (var implied in implies[operation])
            {
                toVisit.Push(implied);
            }
        }

        return effective;
    }

    /// <summary>
    /// Returns one cycle as a path that starts and ends at the same operation (e.g. A, B, A),
    /// or null if there is none. Registration uses it to reject an enum whose [Implies] loop.
    /// </summary>
    public static IReadOnlyList<string>? FindCycle(ILookup<string, string> implies)
    {
        // Operations already fully explored without finding a cycle; no need to walk them again.
        var noCycleFrom = new HashSet<string>();

        foreach (var start in implies.Select(group => group.Key))
        {
            var cycle = FindCycleFrom(start, implies, path: [], noCycleFrom);
            if (cycle is not null)
            {
                return cycle;
            }
        }

        return null;
    }

    // Depth-first walk. `path` is the route from the start to here; reaching an operation that is already
    // on the route means we went in a circle.
    private static List<string>? FindCycleFrom(
        string operation, ILookup<string, string> implies, List<string> path, HashSet<string> noCycleFrom)
    {
        var earlier = path.IndexOf(operation);
        if (earlier >= 0)
        {
            var cycle = path.GetRange(earlier, path.Count - earlier);
            cycle.Add(operation);
            return cycle;
        }

        if (noCycleFrom.Contains(operation))
        {
            return null;
        }

        path.Add(operation);
        foreach (var implied in implies[operation])
        {
            var cycle = FindCycleFrom(implied, implies, path, noCycleFrom);
            if (cycle is not null)
            {
                return cycle;
            }
        }

        path.RemoveAt(path.Count - 1);
        noCycleFrom.Add(operation);
        return null;
    }
}
