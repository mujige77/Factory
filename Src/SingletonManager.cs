﻿using Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Utils.Dbg;

namespace Utils
{
    /// <summary>
    /// Defines a singleton.
    /// </summary>
    public class SingletonDef
    {
        /// <summary>
        /// The type of the singleton.
        /// </summary>
        public Type singletonType;

        /// <summary>
        /// Names of dependencies that the singleton satisfies.
        /// </summary>
        public string[] dependencyNames;

        /// <summary>
        /// Set to true to make the singleton lazy. It will only be instantiated when resolved the first time.
        /// </summary>
        public bool lazy;

        public override string ToString()
        {
            return singletonType.Name + " (" + (lazy ? "lazy" : "normal") + ") [" + dependencyNames.Join(",") + "]";
        }
    }

    /// <summary>
    /// Interface to the singleton manager (for mocking).
    /// </summary>
    public interface ISingletonManager
    {
        /// <summary>
        /// Singletons that are instantiated.
        /// </summary>
        object[] Singletons { get; }

        /// <summary>
        /// Register a singleton with the singleton manager.
        /// The singleton will be instantiated when InstantiateSingletons is called.
        /// </summary>
        void RegisterSingleton(SingletonDef singletonDef);

        /// <summary>
        /// Instantiate all known (non-lazy) singletons.
        /// </summary>
        void InstantiateSingletons(IFactory factory);

        /// <summary>
        /// Start singletons that are startable.
        /// </summary>
        void Start();

        /// <summary>
        /// Shutdown started singletons.
        /// </summary>
        void Shutdown();
    }

    /// <summary>
    /// Manages singletons.
    /// </summary>
    public class SingletonManager : ISingletonManager, IDependencyProvider
    {
        /// <summary>
        /// Singletons that are instantiated.
        /// </summary>
        public object[] Singletons { get; private set; }

        /// <summary>
        /// For mockable C# reflection services.
        /// </summary>
        private IReflection reflection;

        /// <summary>
        /// Factory used to instantiate singletons.
        /// </summary>
        private IFactory factory;

        /// <summary>
        /// Map that allows singletons to be dependency injected.
        /// Maps 'dependency name' to singleton object.
        /// </summary>
        private Dictionary<string, object> dependencyCache = new Dictionary<string, object>();

        /// <summary>
        /// For logging.
        /// </summary>
        private ILogger logger;

        /// <summary>
        /// Definitions for all known (non-lazy) singletons.
        /// </summary>
        private List<SingletonDef> singletonDefs = new List<SingletonDef>();

        public SingletonManager(IReflection reflection, ILogger logger, IFactory factory)
        {
            Argument.NotNull(() => reflection);
            Argument.NotNull(() => logger);
            Argument.NotNull(() => factory);

            this.reflection = reflection;
            this.logger = logger;
            this.factory = factory;
            this.Singletons = new object[0];
        }

        /// <summary>
        /// Register a singleton with the singleton manager.
        /// The singleton will be instantiated when InstantiateSingletons is called.
        /// </summary>
        public void RegisterSingleton(SingletonDef singletonDef)
        {
            Argument.NotNull(() => singletonDef);
            Argument.NotNull(() => singletonDef.singletonType);

            singletonDefs.Add(singletonDef);
        }

        /// <summary>
        /// Resolve a singleton that matches the requested dependency name.
        /// Returns null if none was found.
        /// </summary>
        public object ResolveDependency(string dependencyName)
        {
            Argument.StringNotNullOrEmpty(() => dependencyName);
            Argument.NotNull(() => factory);

            object singleton;
            if (!dependencyCache.TryGetValue(dependencyName, out singleton))
            {
                // See if we can lazy init the singleton.
                var lazySingletonDef = singletonDefs
                    .Where(singletonDef => singletonDef.lazy)
                    .Where(singletonDef => singletonDef.dependencyNames.Contains(dependencyName))
                    .FirstOrDefault();
                if (lazySingletonDef != null)
                {
                    return InstantiateSingleton(lazySingletonDef, factory);
                }
                return null;
            }

            return singleton;
        }

        /// <summary>
        /// Find the type of a singleton that matches the requested dependency name.
        /// Returns null if none was found.
        /// </summary>
        public Type FindDependencyType(string dependencyName)
        {
            Argument.StringNotNullOrEmpty(() => dependencyName);

            return singletonDefs
                .Where(singletonDef => singletonDef.dependencyNames.Contains(dependencyName))
                .WhereMultiple((matchingSingletonDefs) =>
                {
                    var msg =
                        "Multiple singleton definitions match dependency " + dependencyName + "\n" +
                        "Matching singletons:\n" +
                        matchingSingletonDefs.Select(def => "\t" + def.singletonType.Name).Join("\n");

                    throw new ApplicationException(msg);
                })
                .Select(singletonDef => singletonDef.singletonType)
                .FirstOrDefault();
        }

        /// <summary>
        /// Instantiate all known (non-lazy) singletons.
        /// </summary>
        public void InstantiateSingletons(IFactory factory)
        {
            Argument.NotNull(() => factory);

            var nonLazySingletons = singletonDefs.Where(singletonDef => !singletonDef.lazy);
            var sortedByDependency = OrderByDeps(nonLazySingletons, factory, logger);

            Singletons = sortedByDependency
                .Select(singletonDef => InstantiateSingleton(singletonDef, factory))
                .ToArray();
        }

        /// <summary>
        /// Instantiate a singleton from a definition.
        /// </summary>
        private object InstantiateSingleton(SingletonDef singletonDef, IFactory factory)
        {
            var type = singletonDef.singletonType;
            logger.LogInfo("Creating singleton: " + type.Name);

            // Instantiate the singleton.
            var singleton = factory.Create(type);
            singletonDef.dependencyNames.Each(dependencyName => dependencyCache.Add(dependencyName, singleton));

            return singleton;
        }

        /// <summary>
        /// Start singletons that are startable.
        /// </summary>
        public void Start()
        {
            Singletons.ForType((IStartable s) => {
                logger.LogInfo("Starting singleton: " + s.GetType().Name);

                try
                {
                    s.Start();
                }
                catch (Exception ex)
                {
                    logger.LogError("Exception thrown on startup of singleton: " + s.GetType().Name, ex);
                }
            });
        }

        /// <summary>
        /// Shutdown started singletons.
        /// </summary>
        public void Shutdown()
        {
            Singletons.ForType((IStartable s) => {
                logger.LogInfo("Stopping singleton: " + s.GetType().Name);

                try
                {
                    s.Shutdown();
                }
                catch (Exception ex)
                {
                    logger.LogError("Exception thrown on shutdown of singleton: " + s.GetType().Name, ex);
                }
            });
        }

        /// <summary>
        /// Order specified types by their dependencies.
        /// </summary>
        public static IEnumerable<SingletonDef> OrderByDeps(IEnumerable<SingletonDef> singletonDefs, IFactory factory, ILogger logger)
        {
            Argument.NotNull(() => singletonDefs);
            Argument.NotNull(() => factory);

            logger.LogInfo("Ordering singletons:");

            var dependencyMap = singletonDefs
                .SelectMany(singletonDef =>
                {
                    return singletonDef.dependencyNames
                        .Select(dependencyName => new 
                        { 
                            Type = singletonDef, 
                            DependencyName = dependencyName 
                        });
                })
                .ToDictionary(i => i.DependencyName, i => i.Type);

            var defsRemaining = new Queue<SingletonDef>(singletonDefs);
            var dependenciesSatisfied = new HashSet<string>();
            var output = new List<SingletonDef>();
            var defsAlreadySeen = new HashSet<SingletonDef>();

            while (defsRemaining.Count > 0)
            {
                var singletonDef = defsRemaining.Dequeue();

                // Get the names of all types that this singleton is dependent on.
                var singletonDependsOn = DetermineDeps(singletonDef.singletonType, factory).ToArray();

                var allDepsSatisfied = singletonDependsOn                                                  
                    .Where(dependencyName => dependencyMap.ContainsKey(dependencyName)) // Only care about dependencies that are satisifed by other singletons.
                    .All(n => dependenciesSatisfied.Contains(n));                       // Have we seen all other singletons yet that this singleton is dependent on?

                if (allDepsSatisfied)
                {
                    output.Add(singletonDef);

                    logger.LogInfo("\t" + singletonDef.singletonType.Name);
                    logger.LogInfo("\t\tImplements: " + singletonDef.dependencyNames.Join(", "));
                    logger.LogInfo("\t\tDepends on:");
                    singletonDependsOn.Each(dependencyName => logger.LogInfo("\t\t\t" + dependencyName));


                    singletonDef.dependencyNames.Each(dependencyName => dependenciesSatisfied.Add(dependencyName));
                }
                else
                {
                    if (defsAlreadySeen.Contains(singletonDef))
                    {
                        throw new ApplicationException("Already seen type: " + singletonDef.singletonType.Name + ", it is possible there is a circular dependency between types.");
                    }

                    // Record the types we have already attempted to process to make sure we are not 
                    // iterating forever on a circular dependency.
                    defsAlreadySeen.Add(singletonDef);

                    defsRemaining.Enqueue(singletonDef);
                }
            }

            return output.ToArray();
        }

        /// <summary>
        /// Find the requested type and its dependencies.
        /// </summary>
        private static IEnumerable<string> FindDeps(string typeName, IFactory factory)
        {
            yield return typeName;

            var registeredType = factory.FindType(typeName);
            if (registeredType != null)
            {
                // Merge in sub-dependencies.
                foreach (var subDep in DetermineDeps(registeredType, factory))
                {
                    yield return subDep;
                }
            }
        }

        /// <summary>
        /// Determine the dependency tree for a particular type.
        /// </summary>
        private static IEnumerable<string> DetermineDeps(Type type, IFactory factory)
        {
            return type    // Start with types of properties.
                .GetProperties()
                .Where(p => ReflectionUtils.PropertyHasAttribute<DependencyAttribute>(p))
                .Select(p => Factory.GetTypeName(p.PropertyType))
                // Merge in constructor parameter types.
                .Concat(
                    type
                        .GetConstructors()
                        .SelectMany(c => c.GetParameters())
                        .Select(p => Factory.GetTypeName(p.ParameterType))
                )
                .Distinct()
                .SelectMany(n => FindDeps(n, factory));
        }
    }
}
