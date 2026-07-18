namespace ArtificeWorks.Domain.Models;

public enum WorkOrderStatus
{
    Intake, // Scheduled
    Scheduled, // InProcess, OnHold
    InProcess, // Inspection, Fault, OnHold
    Inspection, // Delivery, Fault
    Delivery, // Completed, OnHold
    Completed, // Terminal
    OnHold, // back where it came from
    Fault, // this will be special, and probably OrderItem-specific. For now, this can be another terminus.
    Cancelled // Terminal — order abandoned before completion; releases any assigned stock.
}