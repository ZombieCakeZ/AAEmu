-- 2.0.1.7 compatibility: Specialty / Hauling trade-pack ratios per zone group.
-- See Docs/Schema_Delta_1_2_vs_2_0_1_7.md.

CREATE TABLE IF NOT EXISTS `specialty_pack_ratio` (
    `zone_group_id`   INT UNSIGNED NOT NULL,
    `ratio`           SMALLINT UNSIGNED NOT NULL DEFAULT 100  COMMENT 'Percentage 0..200',
    `computed_at`     DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (`zone_group_id`)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4;
