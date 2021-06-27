using System;
using System.Collections;
using System.Collections.Generic;
using MLAPI.NetworkVariable;
using NUnit.Framework;
using Unity.PerformanceTesting;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using static ExecuteStepInContext;
using Object = UnityEngine.Object;

namespace MLAPI.MultiprocessRuntimeTests
{
    public class NetworkVariablePerformanceTests : BaseMultiprocessTests
    {
        protected override int NbWorkers { get; } = 1;
        private const int k_MaxObjectsToSpawn = 10000;
        private List<OneNetVar> m_ServerSpawnedObjects = new List<OneNetVar>();
        private static GameObjectPool<OneNetVar> s_ServerObjectPool;
        private CustomPrefabSpawnerForPerformanceTests<OneNetVar> m_ClientPrefabHandler;
        private OneNetVar m_PrefabToSpawn;
        protected override bool m_IsPerformanceTest => true;

        private class OneNetVar : NetworkBehaviour
        {
            public static int nbInstances;
            public NetworkVariableInt oneInt = new NetworkVariableInt();

            public void Init()
            {
                nbInstances++;
                if (IsServer)
                {
                    oneInt.Value = 1;
                }
            }

            public static void Stop()
            {
                nbInstances--;
            }
        }

        [OneTimeSetUp]
        public override void SetupTestFixture()
        {
            base.SetupTestFixture();
            SceneManager.sceneLoaded += OnSceneLoadedInitSetupSuite;
        }

        private void OnSceneLoadedInitSetupSuite(Scene scene, LoadSceneMode loadSceneMode)
        {
            InitPrefab();
            s_ServerObjectPool = new GameObjectPool<OneNetVar>();
            s_ServerObjectPool.Init(k_MaxObjectsToSpawn, m_PrefabToSpawn);
        }

        private void InitPrefab()
        {
            var prefabCopy = Object.Instantiate(PrefabReference.Instance.referencedPrefab);
            m_PrefabToSpawn = prefabCopy.AddComponent<OneNetVar>();
        }

        [UnityTest, Performance, MultiprocessContextBasedTest]
        public IEnumerator TestSpawningManyObjects([Values(1, 1000, 2000, 10000)] int nbObjects)
        {
            InitContextSteps();

            // if (!TestCoordinator.Instance.isRegistering)
            // {
            //     yield return new WaitForSeconds(20); // uncomment to be able to attach debugger and profiler to running build
            // }

            yield return new ExecuteStepInContext(StepExecutionContext.Server, _ =>
            {
                Assert.LessOrEqual(nbObjects, k_MaxObjectsToSpawn); // sanity check
            });

            yield return new ExecuteStepInContext(StepExecutionContext.Clients, stepToExecute: nbObjectsBytes =>
            {
                // setup clients
                InitPrefab();
                var targetCount = BitConverter.ToInt32(nbObjectsBytes, 0);

                m_ClientPrefabHandler = new CustomPrefabSpawnerForPerformanceTests<OneNetVar>(m_PrefabToSpawn, k_MaxObjectsToSpawn, SetupSpawnedObject, StopSpawnedObject);
                var hasAddedHandler = NetworkManager.Singleton.PrefabHandler.AddHandler(m_PrefabToSpawn.NetworkObject, m_ClientPrefabHandler);
                Assert.That(hasAddedHandler);

                // add client side reporter for later spawn steps
                void Update(float deltaTime)
                {
                    var count = OneNetVar.nbInstances;
                    if (count > 0)
                    {
                        TestCoordinator.Instance.WriteTestResultsServerRpc(count);

                        if (count >= targetCount)
                        {
                            // we got what we want, don't update results any longer
                            NetworkManager.Singleton.gameObject.GetComponent<CallbackComponent>().OnUpdate -= Update;
                        }
                    }
                }
                NetworkManager.Singleton.gameObject.GetComponent<CallbackComponent>().OnUpdate += Update;
            }, paramToPass: BitConverter.GetBytes(nbObjects));

            yield return new ExecuteStepInContext(StepExecutionContext.Server, _ =>
            {
                // start test
                using (Measure.Scope($"Time Taken For Spawning {nbObjects} objects server side and getting report"))
                {
                    // spawn prefabs for test
                    var totalAllocSampleGroup = new SampleGroup("GC Alloc", SampleUnit.Kilobyte);
                    var beforeAllocatedMemory = Profiler.GetTotalAllocatedMemoryLong();
                    Measure.Custom(totalAllocSampleGroup, beforeAllocatedMemory / 1024);
                    for (int i = 0; i < nbObjects; i++)
                    {
                        var spawnedObject = s_ServerObjectPool.Get();
                        SetupSpawnedObject(spawnedObject);
                        spawnedObject.NetworkObject.Spawn(destroyWithScene: true);
                        m_ServerSpawnedObjects.Add(spawnedObject);
                    }

                    var afterAllocatedMemory = Profiler.GetTotalAllocatedMemoryLong();
                    Measure.Custom(totalAllocSampleGroup, afterAllocatedMemory / 1024);
                    var diffAllocSampleGroup = new SampleGroup("GC Alloc diff for Spawn Server side", SampleUnit.Byte);
                    Measure.Custom(diffAllocSampleGroup, afterAllocatedMemory - beforeAllocatedMemory);

                }
            }, additionalIsFinishedWaiter: () =>
            {
                // wait for spawn results coming from clients
                int finishedCount = 0;
                if (TestCoordinator.AllClientIdsWithResults.Count != NbWorkers)
                {
                    return false;
                }

                foreach (var clientIdWithResult in TestCoordinator.AllClientIdsWithResults)
                {
                    var latestResult = TestCoordinator.PeekLatestResult(clientIdWithResult);
                    if (latestResult == nbObjects)
                    {
                        finishedCount++;
                    }
                }

                return finishedCount == NbWorkers;
            });
            float serverLastResult = 0;
            yield return new ExecuteStepInContext(StepExecutionContext.Server, bytes =>
            {
                // add measurements
                // todo add more client-side metrics like memory usage, time taken to execute, etc
                var allocated = new SampleGroup("NbSpawnedPerFrame client side", SampleUnit.Undefined);

                foreach (var clientId in TestCoordinator.AllClientIdsWithResults)
                {
                    var lastResult = TestCoordinator.PeekLatestResult(clientId);
                    Assert.That(lastResult, Is.EqualTo(nbObjects));
                }

                Assert.That(TestCoordinator.AllClientIdsWithResults.Count, Is.EqualTo(NbWorkers));
                foreach (var (clientId, result) in TestCoordinator.ConsumeCurrentResult())
                {
                    Measure.Custom(allocated, result);
                    serverLastResult = result;
                }

            });
            yield return new ExecuteStepInContext(StepExecutionContext.Clients, nbObjectsBytes =>
            {
                var nbObjectsParam = BitConverter.ToInt32(nbObjectsBytes, 0);
                Assert.That(GameObject.FindObjectsOfType(typeof(OneNetVar)).Length, Is.EqualTo(nbObjectsParam+1), "Wrong number of spawned objects client side"); // +1 for the prefab to spawn
            }, paramToPass:BitConverter.GetBytes(nbObjects));
            yield return new ExecuteStepInContext(StepExecutionContext.Server, bytes =>
            {
                Debug.Log($"finished with test for {nbObjects} expected objects and got {serverLastResult} objects");
            });

        }

        [UnityTearDown, MultiprocessContextBasedTest]
        public IEnumerator UnityTeardown()
        {
            InitContextSteps();

            yield return new ExecuteStepInContext(StepExecutionContext.Server, bytes =>
            {
                foreach (var spawnedObject in m_ServerSpawnedObjects)
                {
                    spawnedObject.NetworkObject.Despawn();
                    s_ServerObjectPool.Release(spawnedObject);
                    StopSpawnedObject(spawnedObject);
                }
                m_ServerSpawnedObjects.Clear();
                s_ServerObjectPool.Dispose();
            });

            yield return new ExecuteStepInContext(StepExecutionContext.Clients, bytes =>
            {
                NetworkManager.Singleton.gameObject.GetComponent<CallbackComponent>().OnUpdate = null; // todo move access to callbackcomponent to singleton

                void UpdateWaitForAllOneNetVarToDespawn(float deltaTime)
                {
                    if (OneNetVar.nbInstances == 0)
                    {
                        NetworkManager.Singleton.gameObject.GetComponent<CallbackComponent>().OnUpdate -= UpdateWaitForAllOneNetVarToDespawn;
                        TestCoordinator.Instance.ClientFinishedServerRpc();
                    }
                }
                NetworkManager.Singleton.gameObject.GetComponent<CallbackComponent>().OnUpdate += UpdateWaitForAllOneNetVarToDespawn;
            }, waitMultipleUpdates: true, ignoreTimeoutException:true); // ignoring timeout since you don't want to hide any issues in the main tests

            yield return new ExecuteStepInContext(StepExecutionContext.Clients, _ =>
            {
                m_ClientPrefabHandler.Dispose();
                NetworkManager.Singleton.PrefabHandler.RemoveHandler(PrefabReference.Instance.referencedPrefab);
            });
        }

        [OneTimeTearDown]
        public override void TeardownSuite()
        {
            base.TeardownSuite();
            SceneManager.sceneLoaded -= OnSceneLoadedInitSetupSuite;
        }

        private static void SetupSpawnedObject(OneNetVar spawnedObject)
        {
            spawnedObject.Init();
        }

        private static void StopSpawnedObject(OneNetVar destroyedObject)
        {
            OneNetVar.Stop();
        }
    }
}
