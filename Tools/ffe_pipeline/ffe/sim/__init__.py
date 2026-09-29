"""Exact event-driven simulation of building-to-building fire spread.

Research plan: Documentation/Research/ResearchPlan_ExactFire.md

Modules
  rng        counter-based random numbers (Philox4x32-10), mirrored in C# and HLSL
  model      physics parameters -> per-edge heat/firebrand coefficients (a scenario)
  scenario   read/write scenario files (.ffes) that the Unity engine runs
  exact      exact event-driven solver (sequential and windowed-parallel) and the
             conventional time-stepped baseline, both numba-compiled
  subset     subset simulation for rare conflagration probabilities
  experiments  verification and RQ1-RQ3 pilot experiments
"""
