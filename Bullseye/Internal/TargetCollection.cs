namespace Bullseye.Internal
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading.Tasks;

    public class TargetCollection : KeyedCollection<string, Target>
    {
        protected override string GetKeyForItem(Target item) => item.Name;

        public async Task RunAsync(List<string> names, bool skipDependencies, bool dryRun, bool parallel, Logger log)
        {
            await log.Running(names).ConfigureAwait(false);
            var stopWatch = Stopwatch.StartNew();

            try
            {
                if (!skipDependencies)
                {
                    this.ValidateDependencies();
                }

                this.Validate(names);

                var targetsRan = new ConcurrentDictionary<string, Task>();
                if (parallel)
                {
                    var tasks = names.Select(name => this.RunAsync(name, skipDependencies, dryRun, parallel, targetsRan, log));
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                else
                {
                    foreach (var name in names)
                    {
                        await this.RunAsync(name, skipDependencies, dryRun, parallel, targetsRan, log).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception)
            {
                await log.Failed(names, stopWatch.Elapsed.TotalMilliseconds).ConfigureAwait(false);
                throw;
            }

            await log.Succeeded(names, stopWatch.Elapsed.TotalMilliseconds).ConfigureAwait(false);
        }

        private async Task RunAsync(string name, bool skipDependencies, bool dryRun, bool parallel, ConcurrentDictionary<string, Task> targetsRan, Logger log)
        {
            var target = this[name];

            if (!skipDependencies)
            {
                if (parallel)
                {
                    var tasks = target.Dependencies.Select(dependency => this.RunAsync(dependency, skipDependencies, dryRun, parallel, targetsRan, log));
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                else
                {
                    foreach (var dependency in target.Dependencies)
                    {
                        await this.RunAsync(dependency, skipDependencies, dryRun, parallel, targetsRan, log).ConfigureAwait(false);
                    }
                }
            }

            await targetsRan.GetOrAdd(name, _ => target.RunAsync(dryRun, parallel, log)).ConfigureAwait(false);
        }

        private void ValidateDependencies()
        {
            var unknownDependencies = new SortedDictionary<string, SortedSet<string>>();

            foreach (var target in this)
            {
                foreach (var dependency in target.Dependencies
                    .Where(dependency => !this.Contains(dependency)))
                {
                    (unknownDependencies.TryGetValue(dependency, out var set)
                            ? set
                            : unknownDependencies[dependency] = new SortedSet<string>())
                        .Add(target.Name);
                }
            }

            if (unknownDependencies.Count != 0)
            {
                var message = $"Missing {(unknownDependencies.Count > 1 ? "dependencies" : "dependency")} detected: " +
                    string.Join(
                        "; ",
                        unknownDependencies.Select(missingDependency =>
                            $"{missingDependency.Key}, required by {missingDependency.Value.Spaced()}"));

                throw new Exception(message);
            }

            var dependencyChain = new Stack<string>();
            foreach (var target in this)
            {
                WalkDependencies(target, dependencyChain);
            }
        }

        private void WalkDependencies(Target target, Stack<string> dependencyChain)
        {
            if (dependencyChain.Contains(target.Name))
            {
                dependencyChain.Push(target.Name);
                throw new Exception($"Circular reference detected: {string.Join(" -> ", dependencyChain.Reverse())}");
            }

            dependencyChain.Push(target.Name);

            foreach (var dependency in target.Dependencies)
            {
                WalkDependencies(this[dependency], dependencyChain);
            }

            dependencyChain.Pop();
        }

        private void Validate(List<string> names)
        {
            var unknownNames = new SortedSet<string>(names.Except(this.Select(target => target.Name)));
            if (unknownNames.Count > 0)
            {
                var message = $"The following target{(unknownNames.Count > 1 ? "s were" : " was")} not found: {unknownNames.Spaced()}.";
                throw new Exception(message);
            }
        }
    }
}
