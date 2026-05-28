-- 2.0.1.7 compatibility: persistent mate (companion) state.
-- See Docs/Schema_Delta_1_2_vs_2_0_1_7.md.

-- No FK to characters(id): characters has composite PK (id, account_id),
-- so a single-column FK can't reference it without an extra index. Cleanup of
-- orphaned mates is the MateManager's job on character delete.
CREATE TABLE IF NOT EXISTS `mates` (
    `id`                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `owner_character_id`  INT UNSIGNED NOT NULL,
    `npc_template_id`     INT UNSIGNED NOT NULL,
    `name`                VARCHAR(64) NOT NULL DEFAULT '',
    `xp`                  INT UNSIGNED NOT NULL DEFAULT 0,
    `level`               SMALLINT UNSIGNED NOT NULL DEFAULT 1,
    `mileage`             INT UNSIGNED NOT NULL DEFAULT 0,
    `hp`                  INT NOT NULL DEFAULT 0,
    `mp`                  INT NOT NULL DEFAULT 0,
    `created_at`          DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    `last_summoned_at`    DATETIME NULL,
    PRIMARY KEY (`id`),
    KEY `idx_mates_owner` (`owner_character_id`)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4;

CREATE TABLE IF NOT EXISTS `mate_equipment` (
    `mate_id`    BIGINT UNSIGNED NOT NULL,
    `slot`       TINYINT UNSIGNED NOT NULL,
    `item_id`    BIGINT UNSIGNED NULL,
    `expires_at` DATETIME NULL,
    PRIMARY KEY (`mate_id`, `slot`),
    CONSTRAINT `fk_mate_equipment_mate`
        FOREIGN KEY (`mate_id`)
        REFERENCES `mates` (`id`)
        ON DELETE CASCADE
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4;
