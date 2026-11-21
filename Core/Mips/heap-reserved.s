# Reserve an additional 0x1000 bytes for custom routines.
# The program heap increment becomes 0x1800 at 0x00126114.
# This is only used together with the third load segment modification.
addiu $v0, $v0, 0x1800
