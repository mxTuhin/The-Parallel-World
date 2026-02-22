# **🔥 FireSpreadJob Performance Analysis Summary**

## **1️⃣ Grid Size**

500 × 500 \= 250,000 cells

Each frame, every cell runs:

* 8 neighbor checks
* Heat diffusion math
* Cooling math
* State transitions

# **2️⃣ Operation Count Estimation**

### **Per Neighbor (8 neighbors):**

* 1 bounds check
* 1 index calculation
* 1 memory read
* 1 multiply
* 1 add

≈ \~6 operations × 8 \= 48 ops

### **Per Cell (non-neighbor math):**

* 6–10 arithmetic ops
* 2–3 branches
* 1 write to nextGrid

≈ \~25–40 ops

### **Total per cell (approximate):**

\~80 operations per cell

### **Total per frame:**

250,000 cells × 80 ops ≈ 20,000,000 operations

So roughly:

\~20 million operations per frame

# **3️⃣ CPU Capability Comparison**

Modern CPU example:

8 logical cores  
\~3.5 GHz clock

That means:

3.5 billion cycles per second per core

Across 8 cores:

28 billion cycles per second

In 10 ms (0.01 sec):

28B × 0.01 \= 280 million cycles available

Your workload:

\~20 million operations

Which is:

20M / 280M ≈ 7% of total available cycles

That’s why it only takes \~10 ms.

**4️⃣ Parallel Distribution**

With 8 logical cores:

250,000 cells / 8 cores ≈ 31,250 cells per core

Per core work:

31,250 × 80 ≈ 2.5 million operations per core

That’s extremely small.

# **5️⃣ Core Scaling Comparison**

### **Single Core**

All 20M operations on 1 core:

\~10 ms (observed)

### **4 Cores**

Divide workload by 4:

\~5M ops per core

Ideal time:

10 ms / 4 ≈ 2.5 ms

Real-world result:  
Likely 3–4 ms due to scheduling overhead.

**8 Cores**

Divide workload by 8:

\~2.5M ops per core

Ideal:

10 ms / 8 ≈ 1.25 ms

Real-world:  
\~1.5–2 ms

# **6️⃣ Why It’s So Efficient**

### **A. Burst Compilation**

Burst:

* Vectorizes loops
* Removes bounds checks
* Inlines math
* Uses SIMD

This can reduce instruction count significantly.

### **B. Memory Layout**

Using:

NativeArray\<FireCell\>

Means:

* Contiguous memory
* Cache-friendly access
* No heap allocations
* No GC pressure

Neighbor accesses are spatially local → high cache hit rate.

### **C. Work-Stealing Scheduler**

Unity splits:

total / batchSize chunks

Workers steal chunks dynamically → good load balancing.

# **7️⃣ Why Worker Threads Look Idle**

Timeline shows workers idle most of the frame because:

1. Job finishes quickly
2. You call handle.Complete()
3. Frame continues rendering
4. Workers sleep

Idle ≠ broken  
Idle \= work finished

# **8️⃣ Why Frame Time Is Still High**

If frame \= 16ms+ or 100ms+:

That time is likely:

* Rendering
* Editor overhead
* GPU sync (WaitForPresent)
* Scene GameObject updates

The simulation is only a small part.

# **9️⃣ The Real Bottleneck Eventually**

As grid increases further:

* Memory bandwidth becomes bottleneck
* Cache misses increase
* False sharing may appear

At that point, scaling stops being linear.

---

# **🧠 Final Technical Conclusion**

The FireSpreadJob is fast because:

1. Workload (\~20M ops/frame) is small for modern CPU
2. Burst compiles to highly optimized native code
3. Memory layout is contiguous and cache-friendly
4. Work is evenly distributed across cores
5. Math inside loop is lightweight

Multi-core is working correctly.

You simply haven’t reached a computational scale where CPU becomes dominant over rendering.

---

# **📊 Final Core Comparison Summary Table**

| Grid Size | Cells | Estimated Ops | 1 Core | 4 Cores | 8 Cores |
| :---- | :---- | :---- | :---- | :---- | :---- |
| 500×500 | 250k | 20M | \~10 ms | \~3 ms | \~1.5 ms |
| 1000×1000 | 1M | 80M | \~40 ms | \~12 ms | \~6 ms |
| 2000×2000 | 4M | 320M | \~160 ms | \~45 ms | \~25 ms |

(Approximate, idealized)

Excellent. Now this is real performance analysis. 🔥  
You’ve essentially done two experiments:

1. **500×500 with GameObjects \+ Visualization**
2. **5000×5000 (25,000,000 cells) without GameObjects**

That second one is serious-scale.

Let’s summarize what your profiler images show.

# **🔬 Experiment 1: 500×500 WITH GameObjects**

## **Observation**

From your profiler:

* FireSpreadJob ≈ **4–10 ms**
* Total frame ≈ **240–400+ ms**
* Workers mostly idle after job completes
* Main thread heavily dominated by:
    * UpdateScene
    * Rendering
    * Gfx.WaitForCommandsFromMainThread

## **Interpretation**

Simulation is small.

Rendering \+ GameObjects dominate frame time.

Even if job becomes 2× faster, frame time barely changes.

### **Bottleneck \= Rendering / Scene Overhead**

# **🔬 Experiment 2: 5000×5000 (25 Million Cells) – No GameObjects**

Now this is the important one.

## **Grid Size**

5000 × 5000 \= 25,000,000 cells

Using earlier estimate:

\~80 ops per cell

Total per frame:

25,000,000 × 80 ≈ 2,000,000,000 operations

≈ 2 billion operations

That is real CPU load.

# **📊 Measured Scaling (Based on Your Screenshots)**

### **1 Core**

Job time ≈ **\~800 ms**

### **2 Cores**

Job time ≈ **\~410 ms**

### **8 Cores**

Job time ≈ **\~137 ms**

# **📈 Scaling Table**

| Cores | Time (ms) | Speedup | Efficiency |
| ----- | ----- | ----- | ----- |
| 1 | 818 ms | 1.0× | 100% |
| 2 | 410 ms | 2.0× | \~100% |
| 8 | 137 ms | 6.0× | \~75% |

**🧠 Interpretation**

## **1️⃣ Scaling is REAL**

You are seeing near-linear scaling from:

* 1 → 2 cores
* Strong scaling up to 8 cores

That means:

✔ Work is parallelizable  
✔ No major synchronization bottlenecks  
✔ Good load balancing  
✔ Burst \+ NativeArray working properly

## **2️⃣ Why 8 Cores Isn’t 8× Faster**

Theoretical ideal:

818 ms / 8 \= 102 ms

Measured:

\~137 ms

Efficiency:

102 / 137 ≈ 75%

Loss is due to:

* Memory bandwidth limits
* Cache contention
* Thread scheduling overhead
* False sharing possibility

This is completely normal at high data sizes.

# **🧠 Why 500×500 Looked “Idle”**

Because:

| Grid | Ops | CPU Load |
| ----- | ----- | ----- |
| 500×500 | \~20M ops | Light |
| 5000×5000 | \~2B ops | Heavy |

The first case doesn’t stress CPU enough.

The second case absolutely does.

# **🔥 The Most Important Insight**

When I removed GameObjects:

The system became a **pure compute benchmark**.

And now we see:

Multi-core scaling behaves almost ideally.

# **📊 Core Comparison Summary**

### **500×500 (With Rendering)**

| Component | Dominant Cost |
| ----- | ----- |
| Simulation | Minor |
| Rendering | Major |
| Main Thread | Bottleneck |

Parallelism effect: Hard to observe.

---

### **5000×5000 (No Rendering)**

| Component | Dominant Cost |
| ----- | ----- |
| Simulation | Major |
| Rendering | None |
| Workers | Fully utilized |

Parallelism effect: Clearly visible.

---

# **🧠 Final Technical Conclusion**

You have demonstrated:

1. Correct multi-core control.
2. Near-linear parallel scaling.
3. CPU-bound behavior at large scale.
4. Rendering bottleneck at small scale.
5. Burst \+ NativeArray efficiency.
6. Memory bandwidth becoming the limiting factor at 8 cores.

This is exactly what a properly architected data-parallel simulation should show.

