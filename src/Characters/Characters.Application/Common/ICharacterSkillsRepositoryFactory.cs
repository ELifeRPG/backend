using ELifeRPG.Shared.Integration.Abstractions;

namespace ELifeRPG.Characters.Application.Common;

/// <summary>
/// Builds a Characters repository bound to a shared cross-module transaction's session instead of
/// this module's normal per-request session — used by an orchestrating command in another module
/// that records skill XP atomically with a write elsewhere (World's gathering orchestrator, task 7:
/// <c>GatherCommand</c> records the skill action and grants the item in one commit, so the two can
/// never diverge). Mirrors <c>Banking.Application.Common.IBankAccountRepositoryFactory</c> exactly.
/// See docs/superpowers/specs/2026-08-15-cross-module-atomic-writes-design.md.
/// </summary>
public interface ICharacterSkillsRepositoryFactory
{
    ICharacterSkillsRepository CreateFor(CrossModuleSessionHandle handle);
}
