namespace IslandBar;

/// <summary>Yay: yarıda kesilince hızını koruyarak yeni hedefe döner.</summary>
internal struct Spring
{
    private const double RestDistance = 0.002;
    private double _velocity;

    public double Value;
    public double Target;

    /// <returns>Hâlâ hareket ediyor mu?</returns>
    public bool Advance(double seconds, double stiffness, double damping, double rest = RestDistance)
    {
        _velocity += ((Target - Value) * stiffness - _velocity * damping) * seconds;
        Value += _velocity * seconds;

        if (Math.Abs(Target - Value) < rest && Math.Abs(_velocity) < rest)
        {
            Jump();
            return false;
        }
        return true;
    }

    public void Jump()
    {
        Value = Target;
        _velocity = 0;
    }

    /// <summary>Dürter: hedef değişmeden çıkar, salınıp geri döner.</summary>
    public void Kick(double velocity)
    {
        _velocity += velocity;
    }

    /// <summary>Elle konumlanır (fareyi izleyen öğe); bırakılınca durduğu yerden yaylanır.</summary>
    public void Hold(double value)
    {
        Value = value;
        _velocity = 0;
    }
}
