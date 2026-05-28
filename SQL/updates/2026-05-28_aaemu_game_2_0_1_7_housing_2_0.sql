-- 2.0.1.7 compatibility: Housing 2.0 — decay timing, Patron tax tracking,
-- demolish-pending flag, per-house tax ledger.
-- See Docs/Schema_Delta_1_2_vs_2_0_1_7.md.

ALTER TABLE `housings`
    ADD COLUMN `decay_started_at`    DATETIME NULL              AFTER `allow_recover`,
    ADD COLUMN `grace_period_until`  DATETIME NULL              AFTER `decay_started_at`,
    ADD COLUMN `demolish_pending`    TINYINT  NOT NULL DEFAULT 0 AFTER `grace_period_until`,
    ADD COLUMN `patron_status`       TINYINT  NOT NULL DEFAULT 0 AFTER `demolish_pending`;

CREATE TABLE IF NOT EXISTS `housing_tax_history` (
    `id`           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `housing_id`   BIGINT UNSIGNED NOT NULL,
    `paid_at`      DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    `amount`       BIGINT UNSIGNED NOT NULL,
    `source`       TINYINT UNSIGNED NOT NULL DEFAULT 0  COMMENT '0=manual,1=auto-debit,2=guild',
    `payer_id`     INT UNSIGNED NULL,
    PRIMARY KEY (`id`),
    KEY `idx_taxhistory_housing` (`housing_id`),
    KEY `idx_taxhistory_paid_at` (`paid_at`)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4;
