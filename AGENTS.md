# Agent notes

## Subagent models
- **Top-level (lead) thread:** spawn subagents on **Claude Opus 5.5, effort high** for implementation and other complex work. Use **Claude Sonnet 5.5, effort high** for less complex tasks and for research.
- **Subagents:** may spawn their own subagents only on **Claude Sonnet 5.5, effort low**, and only for research (reading, searching, summarising). Never for implementation or anything that uses the game.
