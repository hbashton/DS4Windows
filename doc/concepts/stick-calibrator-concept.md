# Concept
Stick drift is an issue most controllers will suffer from at some point in their life, therefore a tool that would
recalibrate it would be very helpful.

## Ways of dealing with the problem
There are potentially 2 ways of dealing with stick drift:

### Window of motion manipulation
This method would be about making the _window_ of motion smaller, in such way that its center aligns with drifted 
stick's center.

#### Implementation
Implementation of this method should be as simple as:
- finding out the amount of units the stick drifts by
- subtracting/adding that amount to the raw input
- making sure the value doesnt fall outside the 0-255 range

#### Tradeoffs
Implementing this would mean that the side opposite to the one in which the stick drifts to would have a
slight deadzone at one end and would never go to the full value at the other end. The example would be:

We find out that the stick in its physically centered state has X axis reading of 132. This means that it drifts by +5 
units, which in turn means that it's aligned slightly to the right. To counteract that, we subtract 5 units from the X 
axis reading.

If we move the stick fully to the left, the value of the axis is 0 (-5, but it falls out of range of 0-255, so 
it's clamped to 0).

However, if we move the stick fully to the right its raw value is 255, but it's translated by the calibrating tool 
to 250.


### Scaling
Another way would be to translate the stick input so that the full window of motion is used, but the input is scaled, 
so that the full range of motion is used (0-255). We would use different coefficients for each side the stick moves in,
so that we avoid having deadzones or unreachable values on either side.

#### Implementation
Implementation of this method should be slightly more complicated than that of window shrinking. It would involve:
- finding out the amount of units the stick drifts by
- the raw value of 127 + drift amount should translate to 127
- calculate 2 coefficients for each side the stick can move in (ranges of `<0; (127 + drift value))` and 
`((127 + drift value); 255>`)
- translate the raw value using the coefficient determined by the current raw value

#### Tradeoffs
Main tradeoff would be the fact of scaling rather than the output being a linear representation of the input, however
the stick drift should never be so big that it becomes a problem or even noticable.


## Pros and cons of each option
The first option should work fairly well in most of the cases given the stick drift is not too big. If it's only a few 
units that it drifts by, the deadzone at the _shorter_ side and lack of the top end at the _longer_ side should not 
even be noticable. It's also easier to implement, as it only involves checking the amount by which the stick drifts 
and then making simple addition/subtraction operations.

The second option will ensure that the output stick value range of 0-255 remains
intact. It might be better for more severe cases of drift, or where the lack of that top-end value would be a problem.
It might add a bit of CPU overhead, however pre-calculating the values and saving them to a Dictionary, and then just
performing a lookup rather than calculating the value in real time might be a good consideration.

## Real world implementation
- In profile settings, in the `Axis Config` tab, create an option to open a calibration tool. 
- It should open a new window
- The window should prompt the user to move the stick determine the physical centered state of the stick by e.g.
flicking it in a few different directions and letting it recenter itself
- The drift values in both axes should be calculated and then saved to a config file
- Appropriate translations should be performed **before any other input manipulation (any settings in the `Axis Config`
tab, like deadzone, sensitivity, etc.)**  