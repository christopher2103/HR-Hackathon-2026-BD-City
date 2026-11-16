using System.Collections;
using UnityEngine;
using UnityEngine.AI;

namespace BubbleFeature.Bots
{
    /// <summary>
    /// A placeholder bot standing in for another visitor: walks to random points
    /// around its home spot on the NavMesh, pauses a moment, and repeats.
    /// Its name is used later by the "Send gift" menu.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class BotWanderer : MonoBehaviour
    {
        [SerializeField] private string displayName = "Bot";
        [SerializeField] private float wanderRadius = 6f;           // metres around its home spot
        [SerializeField] private Vector2 pauseSeconds = new Vector2(1f, 4f);

        private NavMeshAgent agent;
        private Vector3 home;

        public string DisplayName => displayName;
        public void SetName(string newName) { displayName = newName; name = $"Bot ({newName})"; }

        private void Awake() => agent = GetComponent<NavMeshAgent>();

        private IEnumerator Start()
        {
            home = transform.position;
            yield return null; // let the NavMesh finish building

            while (true)
            {
                if (agent.isOnNavMesh && TryRandomPoint(out Vector3 point))
                {
                    agent.SetDestination(point);
                    // Walk until arrived (or stuck for too long).
                    float timeout = 20f;
                    while (timeout > 0f && (agent.pathPending || agent.remainingDistance > agent.stoppingDistance + 0.1f))
                    {
                        timeout -= Time.deltaTime;
                        yield return null;
                    }
                }
                yield return new WaitForSeconds(Random.Range(pauseSeconds.x, pauseSeconds.y));
            }
        }

        private bool TryRandomPoint(out Vector3 point)
        {
            for (int i = 0; i < 10; i++)
            {
                Vector2 offset = Random.insideUnitCircle * wanderRadius;
                Vector3 candidate = home + new Vector3(offset.x, 0f, offset.y);
                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 1.5f, NavMesh.AllAreas))
                {
                    point = hit.position;
                    return true;
                }
            }
            point = transform.position;
            return false;
        }
    }
}
