-- 2.0.1.7 compatibility: per-character actability (non-combat XP) state.
-- See Docs/Schema_Delta_1_2_vs_2_0_1_7.md for the rationale.

CREATE TABLE IF NOT EXISTS `character_actability` (
    `character_id`   INT UNSIGNED NOT NULL,
    `actability_id`  INT UNSIGNED NOT NULL,
    `xp`             INT UNSIGNED NOT NULL DEFAULT 0,
    `step`           SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    `expert_step`    SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    `updated_at`     DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (`character_id`, `actability_id`),
    CONSTRAINT `fk_charactability_character`
        FOREIGN KEY (`character_id`)
        REFERENCES `characters` (`id`)
        ON DELETE CASCADE
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4;

-- Per-character cap override (Expert system in 2.0). NULL = use default cap.
ALTER TABLE `characters`
    ADD COLUMN `expert_limit_overrides` JSON NULL AFTER `expanded_expert`;
