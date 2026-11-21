# Shield guard cave at 0x001481C8, quarter-Might setting (rule 10, setting 1).
# The final store is the delay slot of the jump back to the battle setup.
# This is a comparison source; the editor continues using the verified existing patch.
.set noreorder
.set noat
.text
.globl shield_guard_quarter
shield_guard_quarter:
    lw $s5, 8($s2)
    lw $v0, 168($s3)
    sra $v1, $v0, 2
    nop
    nop
    subu $v0, $v0, $v1
    sw $v0, 168($s3)
    lw $at, 108($s3)
    addu $at, $at, $v1
    sw $at, 108($s3)
    lw $at, 104($s3)
    addu $at, $at, $v1
    j 0x0022C2C8
    sw $at, 104($s3)
