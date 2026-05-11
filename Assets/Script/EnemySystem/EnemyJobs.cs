using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Jobs;
using UnityEngine.Jobs;

/// <summary>
/// Burst-compiled job that moves all enemy transforms toward the player in parallel.
/// Scheduled via IJobParallelForTransform so Unity feeds each transform on its own thread.
/// </summary>
[BurstCompile]
public struct EnemyMoveJob : IJobParallelForTransform
{
    public float3 PlayerPosition;
    public float MoveSpeed;
    public float DeltaTime;

    public void Execute(int index, TransformAccess transform)
    {
        float3 pos = transform.position;
        float3 toPlayer = new float3(PlayerPosition.x - pos.x, 0f, PlayerPosition.z - pos.z);
        float dist = math.length(toPlayer);

        if (dist < 0.5f) return;

        float3 dir = toPlayer / dist;

        // Y is never touched — enemies slide on their own horizontal plane only
        transform.position = new float3(
            pos.x + dir.x * MoveSpeed * DeltaTime,
            pos.y,
            pos.z + dir.z * MoveSpeed * DeltaTime
        );
        transform.rotation = quaternion.LookRotationSafe(dir, math.up());
    }
}

/// <summary>
/// Burst-compiled job that finds the nearest living enemy within range.
/// Runs synchronously on demand (schedule + immediate Complete) so the result
/// is available without a round-trip to the next frame.
/// </summary>
[BurstCompile]
public struct FindNearestTargetJob : IJob
{
    [ReadOnly] public NativeArray<float3> EnemyPositions;
    public float3 PlayerPosition;
    public float MaxRange;
    // Single-element array used as output (avoids NativeReference dependency)
    public NativeArray<int> ResultIndex;

    public void Execute()
    {
        int nearest = -1;
        float nearestDistSq = MaxRange * MaxRange;

        for (int i = 0; i < EnemyPositions.Length; i++)
        {
            float3 diff = EnemyPositions[i] - PlayerPosition;
            diff.y = 0f;
            float distSq = math.lengthsq(diff);
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearest = i;
            }
        }

        ResultIndex[0] = nearest;
    }
}
